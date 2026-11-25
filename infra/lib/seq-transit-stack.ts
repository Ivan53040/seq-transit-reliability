import { CfnOutput, Duration, Fn, RemovalPolicy, Stack, TimeZone, type StackProps } from 'aws-cdk-lib';
import * as apigwv2 from 'aws-cdk-lib/aws-apigatewayv2';
import { HttpLambdaIntegration } from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as budgets from 'aws-cdk-lib/aws-budgets';
import * as cloudfront from 'aws-cdk-lib/aws-cloudfront';
import * as origins from 'aws-cdk-lib/aws-cloudfront-origins';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as cwActions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as s3deploy from 'aws-cdk-lib/aws-s3-deployment';
import * as scheduler from 'aws-cdk-lib/aws-scheduler';
import * as schedulerTargets from 'aws-cdk-lib/aws-scheduler-targets';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as subscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import type { Construct } from 'constructs';
import type { AppConfig } from './config';
import { databaseUserArn } from './database-arn';

/** Database roles created by db/migrations/0002_app_roles.sql. */
const INGEST_DB_USER = 'seq_ingest';
const API_DB_USER = 'seq_api';
const METRIC_NAMESPACE = 'SeqTransit';
const BEDROCK_MODEL_ID = 'amazon.nova-micro-v1:0';
/** Long enough for a settled service day; the API's Cache-Control decides the actual lifetime. */
const API_MAX_CACHE = Duration.days(1);
/** Everything is same-origin except the OpenStreetMap tiles; Leaflet sets inline styles. */
const CONTENT_SECURITY_POLICY = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: https://tile.openstreetmap.org",
  "font-src 'self'",
  "connect-src 'self'",
  "frame-ancestors 'none'",
  "base-uri 'self'",
  "form-action 'self'",
].join('; ');

export interface SeqTransitStackProps extends StackProps {
  readonly config: AppConfig;
}

/**
 * Everything except the database, which is an Aurora PostgreSQL cluster created with
 * "express configuration" (see scripts/create-database). That cluster lives outside any VPC
 * and is reached through Aurora's internet access gateway with IAM sign-in, so none of these
 * Lambda functions need a VPC, and the stack needs no NAT gateway.
 */
export class SeqTransitStack extends Stack {
  constructor(scope: Construct, id: string, props: SeqTransitStackProps) {
    super(scope, id, props);
    const { config } = props;

    // ---------- Raw snapshots ----------
    const snapshots = new s3.Bucket(this, 'SnapshotBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      lifecycleRules: [{ expiration: Duration.days(config.rawSnapshotRetentionDays) }],
      // Portfolio project: `cdk destroy` should leave nothing behind that keeps costing money.
      removalPolicy: RemovalPolicy.DESTROY,
      autoDeleteObjects: true,
    });

    // ---------- Lambda functions (.NET 10 on Graviton) ----------
    const ingestionCode = lambda.Code.fromAsset(config.artifacts.ingestion);
    const dotnet = {
      runtime: lambda.Runtime.DOTNET_10,
      architecture: lambda.Architecture.ARM_64,
    };

    const fetchLogs = this.logGroup('FetchLogs');
    const fetchFn = new lambda.Function(this, 'FetchFunction', {
      ...dotnet,
      description: 'Every minute: download the Translink TripUpdates feed and store it in S3',
      code: ingestionCode,
      handler: 'SeqTransit.Ingestion::SeqTransit.Ingestion.FetchFunction::HandleAsync',
      memorySize: 512,
      timeout: Duration.seconds(50),
      logGroup: fetchLogs,
      environment: {
        SNAPSHOT_BUCKET: snapshots.bucketName,
        STALE_AFTER_SECONDS: '300',
      },
    });
    snapshots.grantPut(fetchFn);

    const loadFn = new lambda.Function(this, 'LoadFunction', {
      ...dotnet,
      description: `Every ${config.loadIntervalMinutes} minutes: load new snapshots from S3 into Aurora PostgreSQL`,
      code: ingestionCode,
      handler: 'SeqTransit.Ingestion::SeqTransit.Ingestion.LoadFunction::HandleAsync',
      memorySize: 1024,
      timeout: Duration.minutes(5),
      // One load at a time. Always enforced by an advisory lock in the function; reserving
      // concurrency as well is optional (see AppConfig.reserveConcurrency).
      reservedConcurrentExecutions: config.reserveConcurrency ? 1 : undefined,
      logGroup: this.logGroup('LoadLogs'),
      environment: {
        SNAPSHOT_BUCKET: snapshots.bucketName,
        DB_HOST: config.dbHost,
        DB_NAME: 'postgres',
        DB_USER: INGEST_DB_USER,
        MAX_SNAPSHOTS_PER_BATCH: String(Math.max(15, config.loadIntervalMinutes)),
      },
    });
    snapshots.grantRead(loadFn);
    this.allowDatabaseSignIn(loadFn, config, INGEST_DB_USER);

    const apiFn = new lambda.Function(this, 'ApiFunction', {
      ...dotnet,
      description: 'ASP.NET Core API (read-only) behind API Gateway',
      code: lambda.Code.fromAsset(config.artifacts.api),
      handler: 'SeqTransit.Api',
      memorySize: 1024,
      timeout: Duration.seconds(30),
      // Optional cap on instances; each instance opens at most DB_MAX_POOL_SIZE connections.
      reservedConcurrentExecutions: config.reserveConcurrency ? 5 : undefined,
      logGroup: this.logGroup('ApiLogs'),
      environment: {
        DB_HOST: config.dbHost,
        DB_NAME: 'postgres',
        DB_USER: API_DB_USER,
        // A Lambda instance serves one request at a time, so two connections are plenty.
        DB_MAX_POOL_SIZE: '2',
        // Two missed loads (plus feed lag) before /api/health reports stale data.
        Health__StaleAfterMinutes: String(config.loadIntervalMinutes * 2 + 15),
      },
    });
    this.allowDatabaseSignIn(apiFn, config, API_DB_USER);

    // ---------- Schedules ----------
    // Fetch never retries: the next run is a minute away anyway.
    new scheduler.Schedule(this, 'FetchEveryMinute', {
      description: 'Store a TripUpdates snapshot every minute',
      schedule: scheduler.ScheduleExpression.rate(Duration.minutes(1)),
      target: new schedulerTargets.LambdaInvoke(fetchFn, { retryAttempts: 0 }),
    });

    // Batching loads lets Aurora Serverless pause between them; see docs/decisions.md.
    new scheduler.Schedule(this, 'LoadSchedule', {
      description: 'Load stored snapshots into the database',
      schedule: scheduler.ScheduleExpression.rate(Duration.minutes(config.loadIntervalMinutes)),
      target: new schedulerTargets.LambdaInvoke(loadFn, { retryAttempts: 1 }),
    });

    // Weekly AI summary via Amazon Bedrock (Phase 8), guarded against invented numbers.
    const summaryLogs = this.logGroup('SummaryLogs');
    const summaryFn = new lambda.Function(this, 'SummaryFunction', {
      ...dotnet,
      description: 'Generate a weekly plain-English reliability summary with Amazon Bedrock',
      code: lambda.Code.fromAsset(config.artifacts.ingestion),
      handler: 'SeqTransit.Ingestion::SeqTransit.Ingestion.WeeklySummaryFunction::HandleAsync',
      memorySize: 512,
      timeout: Duration.seconds(60),
      logGroup: summaryLogs,
      environment: {
        DB_HOST: config.dbHost,
        DB_NAME: 'postgres',
        DB_USER: INGEST_DB_USER,
        BEDROCK_MODEL_ID,
      },
    });
    this.allowDatabaseSignIn(summaryFn, config, INGEST_DB_USER);
    summaryFn.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ['bedrock:InvokeModel'],
        resources: [`arn:${this.partition}:bedrock:${this.region}::foundation-model/${BEDROCK_MODEL_ID}`],
      }),
    );

    new scheduler.Schedule(this, 'WeeklySummarySchedule', {
      description: 'Generate the weekly reliability summary every Monday at 7 am Brisbane time',
      schedule: scheduler.ScheduleExpression.cron({
        minute: '0',
        hour: '7',
        weekDay: 'MON',
        timeZone: TimeZone.AUSTRALIA_BRISBANE,
      }),
      target: new schedulerTargets.LambdaInvoke(summaryFn, { retryAttempts: 1 }),
    });

    // ---------- HTTP API ----------
    const httpApi = new apigwv2.HttpApi(this, 'HttpApi', {
      description: 'SEQ Transit Reliability API',
      defaultIntegration: new HttpLambdaIntegration('ApiIntegration', apiFn),
      createDefaultStage: false,
    });
    new apigwv2.HttpStage(this, 'DefaultStage', {
      httpApi,
      stageName: '$default',
      autoDeploy: true,
      // A public demo should not be able to run up the bill or overload the database.
      throttle: { rateLimit: 10, burstLimit: 20 },
    });

    // ---------- Website: S3 + CloudFront, with /api/* routed to API Gateway ----------
    const site = new s3.Bucket(this, 'SiteBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      removalPolicy: RemovalPolicy.DESTROY,
      autoDeleteObjects: true,
    });

    // Cache API responses at the edge: every cache hit is a request the database never sees.
    // The API sets Cache-Control (src/SeqTransit.Api/ApiCacheHeaders.cs): a day for settled
    // service days, five minutes for today, no-store for health and errors. The default TTL only
    // applies to a response without the header.
    const apiCache = new cloudfront.CachePolicy(this, 'ApiCachePolicy', {
      comment: 'Read-only API responses; lifetime set by the API Cache-Control header',
      defaultTtl: Duration.seconds(60),
      minTtl: Duration.seconds(0),
      maxTtl: API_MAX_CACHE,
      queryStringBehavior: cloudfront.CacheQueryStringBehavior.all(),
      enableAcceptEncodingGzip: true,
      enableAcceptEncodingBrotli: true,
    });

    // Client-side routes (/routes/66-1) have no object in S3. Rewrite any extensionless path to
    // /index.html at the viewer edge so React Router can render it. This only runs on the default
    // (S3) behaviour; distribution-level errorResponses would also rewrite /api/* 404s into HTML.
    const spaRewrite = new cloudfront.Function(this, 'SpaRewriteFunction', {
      comment: 'Serve index.html for client-side routes',
      runtime: cloudfront.FunctionRuntime.JS_2_0,
      code: cloudfront.FunctionCode.fromInline(`
function handler(event) {
  var request = event.request;
  var lastSegment = request.uri.split('/').pop();
  if (lastSegment.indexOf('.') === -1) {
    request.uri = '/index.html';
  }
  return request;
}`),
    });

    // Security headers for the page and the API. The CSP is report-only until it has been checked
    // in a browser (Leaflet tiles, inline styles); switch it to enforcing after that.
    const securityHeaders = new cloudfront.ResponseHeadersPolicy(this, 'SecurityHeadersPolicy', {
      comment: 'HSTS, nosniff, frame and referrer policy, report-only CSP',
      securityHeadersBehavior: {
        strictTransportSecurity: { accessControlMaxAge: Duration.days(365), includeSubdomains: true, override: true },
        contentTypeOptions: { override: true },
        frameOptions: { frameOption: cloudfront.HeadersFrameOption.DENY, override: true },
        referrerPolicy: { referrerPolicy: cloudfront.HeadersReferrerPolicy.STRICT_ORIGIN_WHEN_CROSS_ORIGIN, override: true },
      },
      customHeadersBehavior: {
        customHeaders: [
          {
            header: 'Content-Security-Policy-Report-Only',
            value: CONTENT_SECURITY_POLICY,
            override: true,
          },
        ],
      },
    });

    const apiDomain = Fn.select(2, Fn.split('/', httpApi.apiEndpoint));
    const distribution = new cloudfront.Distribution(this, 'SiteDistribution', {
      comment: 'SEQ Transit Reliability',
      defaultRootObject: 'index.html',
      priceClass: cloudfront.PriceClass.PRICE_CLASS_ALL,
      defaultBehavior: {
        origin: origins.S3BucketOrigin.withOriginAccessControl(site),
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        cachePolicy: cloudfront.CachePolicy.CACHING_OPTIMIZED,
        responseHeadersPolicy: securityHeaders,
        functionAssociations: [
          { function: spaRewrite, eventType: cloudfront.FunctionEventType.VIEWER_REQUEST },
        ],
      },
      additionalBehaviors: {
        '/api/*': {
          origin: new origins.HttpOrigin(apiDomain),
          viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
          allowedMethods: cloudfront.AllowedMethods.ALLOW_GET_HEAD_OPTIONS,
          cachePolicy: apiCache,
          responseHeadersPolicy: securityHeaders,
          originRequestPolicy: cloudfront.OriginRequestPolicy.ALL_VIEWER_EXCEPT_HOST_HEADER,
        },
      },
      // Do not cache gateway errors (a 503 while Aurora resumes, a 429 from throttling): CloudFront
      // would otherwise hold them for 10 seconds and the dashboard's retries would get the same error.
      // No responsePagePath, so API errors are passed through unchanged, never rewritten into HTML.
      errorResponses: [500, 502, 503, 504].map((httpStatus) => ({ httpStatus, ttl: Duration.seconds(0) })),
    });

    new s3deploy.BucketDeployment(this, 'DeploySite', {
      sources: [s3deploy.Source.asset(config.artifacts.web)],
      destinationBucket: site,
      distribution,
      distributionPaths: ['/*'],
    });

    // ---------- Monitoring ----------
    const alerts = new sns.Topic(this, 'AlertTopic', { displayName: 'SEQ Transit Reliability alerts' });
    if (config.alertEmail) {
      alerts.addSubscription(new subscriptions.EmailSubscription(config.alertEmail));
    }
    const notify = new cwActions.SnsAction(alerts);

    // The fetch function logs a fixed marker on every success; count those lines.
    const snapshotsStored = new logs.MetricFilter(this, 'SnapshotsStoredFilter', {
      logGroup: fetchLogs,
      filterPattern: logs.FilterPattern.literal('"SNAPSHOT_STORED"'),
      metricNamespace: METRIC_NAMESPACE,
      metricName: 'SnapshotsStored',
      metricValue: '1',
    }).metric({ statistic: 'Sum', period: Duration.minutes(5) });

    const feedStale = new logs.MetricFilter(this, 'FeedStaleFilter', {
      logGroup: fetchLogs,
      filterPattern: logs.FilterPattern.literal('"FEED_STALE"'),
      metricNamespace: METRIC_NAMESPACE,
      metricName: 'FeedStale',
      metricValue: '1',
    }).metric({ statistic: 'Sum', period: Duration.minutes(10) });

    const summaryRejected = new logs.MetricFilter(this, 'SummaryRejectedFilter', {
      logGroup: summaryLogs,
      filterPattern: logs.FilterPattern.literal('"WEEKLY_SUMMARY_REJECTED"'),
      metricNamespace: METRIC_NAMESPACE,
      metricName: 'WeeklySummaryRejected',
      metricValue: '1',
    }).metric({ statistic: 'Sum', period: Duration.hours(1) });

    const alarms = [
      new cloudwatch.Alarm(this, 'NoSnapshotsAlarm', {
        alarmDescription: 'Fewer than 3 snapshots stored in 5 minutes: the fetch pipeline is failing.',
        metric: snapshotsStored,
        threshold: 3,
        comparisonOperator: cloudwatch.ComparisonOperator.LESS_THAN_THRESHOLD,
        evaluationPeriods: 1,
        treatMissingData: cloudwatch.TreatMissingData.BREACHING,
      }),
      new cloudwatch.Alarm(this, 'FeedStaleAlarm', {
        alarmDescription: 'The Translink feed timestamp has been more than 5 minutes old for most of the last 10 minutes.',
        metric: feedStale,
        threshold: 5,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        evaluationPeriods: 1,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      }),
      new cloudwatch.Alarm(this, 'LoadErrorsAlarm', {
        alarmDescription: 'The load function failed on two consecutive runs.',
        metric: loadFn.metricErrors({ statistic: 'Sum', period: Duration.minutes(config.loadIntervalMinutes) }),
        threshold: 1,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        evaluationPeriods: 2,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      }),
      new cloudwatch.Alarm(this, 'ApiErrorsAlarm', {
        alarmDescription: 'The HTTP API returned 5xx errors 5 or more times in 5 minutes.',
        metric: httpApi.metricServerError({ statistic: 'Sum', period: Duration.minutes(5) }),
        threshold: 5,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        evaluationPeriods: 1,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      }),
      new cloudwatch.Alarm(this, 'SummaryRejectedAlarm', {
        alarmDescription: 'The weekly summary was rejected by the numeric guardrail; last week keeps the previous summary.',
        metric: summaryRejected,
        threshold: 1,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        evaluationPeriods: 1,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      }),
      new cloudwatch.Alarm(this, 'SummaryErrorsAlarm', {
        alarmDescription: 'The weekly summary function failed (Bedrock or database error).',
        metric: summaryFn.metricErrors({ statistic: 'Sum', period: Duration.hours(1) }),
        threshold: 1,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        evaluationPeriods: 1,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      }),
    ];
    for (const alarm of alarms) {
      alarm.addAlarmAction(notify);
      alarm.addOkAction(notify);
    }

    // ---------- Cost guard ----------
    if (config.alertEmail) {
      const subscribers = [{ subscriptionType: 'EMAIL', address: config.alertEmail }];
      new budgets.CfnBudget(this, 'MonthlyBudget', {
        budget: {
          budgetName: 'seq-transit-reliability-monthly',
          budgetType: 'COST',
          timeUnit: 'MONTHLY',
          budgetLimit: { amount: config.monthlyBudgetUsd, unit: 'USD' },
        },
        notificationsWithSubscribers: [
          {
            notification: { notificationType: 'ACTUAL', comparisonOperator: 'GREATER_THAN', threshold: 50, thresholdType: 'PERCENTAGE' },
            subscribers,
          },
          {
            notification: { notificationType: 'FORECASTED', comparisonOperator: 'GREATER_THAN', threshold: 100, thresholdType: 'PERCENTAGE' },
            subscribers,
          },
        ],
      });
    }

    // ---------- Outputs ----------
    new CfnOutput(this, 'SiteUrl', { value: `https://${distribution.distributionDomainName}` });
    new CfnOutput(this, 'ApiUrl', { value: httpApi.apiEndpoint });
    new CfnOutput(this, 'SnapshotBucketName', { value: snapshots.bucketName });
    new CfnOutput(this, 'AlertTopicArn', { value: alerts.topicArn });
  }

  private logGroup(id: string): logs.LogGroup {
    return new logs.LogGroup(this, id, {
      retention: logs.RetentionDays.ONE_MONTH,
      removalPolicy: RemovalPolicy.DESTROY,
    });
  }

  /** IAM database authentication, limited to one database user on one cluster. */
  private allowDatabaseSignIn(fn: lambda.Function, config: AppConfig, dbUser: string): void {
    fn.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ['rds-db:connect'],
        resources: [databaseUserArn(this, config.dbClusterResourceId, dbUser)],
      }),
    );
  }
}
