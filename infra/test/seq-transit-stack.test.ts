import { strict as assert } from 'node:assert';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { App } from 'aws-cdk-lib';
import { Match, Template } from 'aws-cdk-lib/assertions';
import type { AppConfig } from '../lib/config';
import { GitHubDeployStack } from '../lib/github-deploy-stack';
import { SeqTransitStack } from '../lib/seq-transit-stack';

// Empty placeholder folders stand in for the real build outputs.
function fakeArtifacts(): AppConfig['artifacts'] {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'seq-transit-'));
  const dirs = { ingestion: path.join(root, 'ingestion'), api: path.join(root, 'api'), web: path.join(root, 'web') };
  for (const dir of Object.values(dirs)) {
    fs.mkdirSync(dir);
    fs.writeFileSync(path.join(dir, 'placeholder.txt'), 'test');
  }
  return dirs;
}

const config: AppConfig = {
  dbHost: 'seq-transit.cluster-abc.ap-southeast-2.rds.amazonaws.com',
  dbClusterResourceId: 'cluster-TESTRESOURCEID',
  alertEmail: 'alerts@example.com',
  monthlyBudgetUsd: 10,
  loadIntervalMinutes: 30,
  rawSnapshotRetentionDays: 30,
  reserveConcurrency: false,
  githubRepository: 'someone/seq-transit-reliability',
  artifacts: fakeArtifacts(),
};

const env = { account: '123456789012', region: 'ap-southeast-2' };
const template = Template.fromStack(new SeqTransitStack(new App(), 'Test', { env, config }));

test('creates no VPC and no NAT gateway', () => {
  template.resourceCountIs('AWS::EC2::VPC', 0);
  template.resourceCountIs('AWS::EC2::NatGateway', 0);
});

test('all four .NET functions run on .NET 10 and Graviton', () => {
  const functions = template.findResources('AWS::Lambda::Function', {
    Properties: { Runtime: 'dotnet10', Architectures: ['arm64'] },
  });
  assert.equal(Object.keys(functions).length, 4);
});

test('weekly summary function invokes only one Bedrock model on Mondays at 7 am Brisbane time', () => {
  template.hasResourceProperties('AWS::Scheduler::Schedule', {
    ScheduleExpression: 'cron(0 7 ? * MON *)',
    ScheduleExpressionTimezone: 'Australia/Brisbane',
  });
  const policies = JSON.stringify(template.findResources('AWS::IAM::Policy'));
  assert.match(policies, /"bedrock:InvokeModel"/);
  assert.match(policies, /foundation-model\/amazon\.nova-micro-v1:0/);
});

test('fetch runs every minute and load every 30 minutes', () => {
  template.hasResourceProperties('AWS::Scheduler::Schedule', { ScheduleExpression: 'rate(1 minute)' });
  template.hasResourceProperties('AWS::Scheduler::Schedule', { ScheduleExpression: 'rate(30 minutes)' });
});

test('database sign-in is limited to the app roles, never the admin user', () => {
  const policies = JSON.stringify(template.findResources('AWS::IAM::Policy'));
  assert.match(policies, /dbuser:cluster-TESTRESOURCEID\/seq_ingest/);
  assert.match(policies, /dbuser:cluster-TESTRESOURCEID\/seq_api/);
  assert.doesNotMatch(policies, /dbuser:cluster-TESTRESOURCEID\/postgres/);
});

test('buckets block all public access', () => {
  const buckets = template.findResources('AWS::S3::Bucket');
  for (const bucket of Object.values(buckets)) {
    assert.deepEqual(bucket.Properties.PublicAccessBlockConfiguration, {
      BlockPublicAcls: true,
      BlockPublicPolicy: true,
      IgnorePublicAcls: true,
      RestrictPublicBuckets: true,
    });
  }
});

test('API is throttled and routed through CloudFront under /api/*', () => {
  template.hasResourceProperties('AWS::ApiGatewayV2::Stage', {
    StageName: '$default',
    DefaultRouteSettings: Match.objectLike({ ThrottlingRateLimit: 10, ThrottlingBurstLimit: 20 }),
  });
  template.hasResourceProperties('AWS::CloudFront::Distribution', {
    DistributionConfig: Match.objectLike({
      CacheBehaviors: Match.arrayWith([Match.objectLike({ PathPattern: '/api/*' })]),
    }),
  });
});

test('client-side routes fall back to index.html without rewriting API errors', () => {
  template.resourceCountIs('AWS::CloudFront::Function', 1);
  template.hasResourceProperties('AWS::CloudFront::Distribution', {
    DistributionConfig: Match.objectLike({
      DefaultCacheBehavior: Match.objectLike({
        FunctionAssociations: [Match.objectLike({ EventType: 'viewer-request' })],
      }),
    }),
  });
  // Error responses only switch off error caching; none may rewrite an API error into a page.
  const distribution = Object.values(template.findResources('AWS::CloudFront::Distribution'))[0];
  const errors = distribution.Properties.DistributionConfig.CustomErrorResponses as Array<Record<string, unknown>>;
  assert.deepEqual(errors.map((e) => e.ErrorCode), [500, 502, 503, 504]);
  for (const error of errors) {
    assert.equal(error.ErrorCachingMinTTL, 0);
    assert.equal(error.ResponsePagePath, undefined);
    assert.equal(error.ResponseCode, undefined);
  }
});

test('API responses may be cached for up to a day, as the API Cache-Control header decides', () => {
  template.hasResourceProperties('AWS::CloudFront::CachePolicy', {
    CachePolicyConfig: Match.objectLike({ DefaultTTL: 60, MaxTTL: 86400 }),
  });
});

test('page and API responses carry security headers', () => {
  template.hasResourceProperties('AWS::CloudFront::ResponseHeadersPolicy', {
    ResponseHeadersPolicyConfig: Match.objectLike({
      SecurityHeadersConfig: Match.objectLike({
        StrictTransportSecurity: Match.objectLike({ AccessControlMaxAgeSec: 31536000 }),
        ContentTypeOptions: { Override: true },
        FrameOptions: { FrameOption: 'DENY', Override: true },
      }),
    }),
  });
  const distribution = JSON.stringify(template.findResources('AWS::CloudFront::Distribution'));
  assert.equal(distribution.match(/ResponseHeadersPolicyId/g)?.length, 2);
});

test('no concurrency is reserved by default, so a new account quota of 10 still deploys', () => {
  const functions = Object.values(template.findResources('AWS::Lambda::Function'));
  for (const fn of functions) {
    assert.equal(fn.Properties.ReservedConcurrentExecutions, undefined);
  }
  template.hasResourceProperties('AWS::Lambda::Function', {
    Handler: 'SeqTransit.Api',
    Environment: { Variables: Match.objectLike({ DB_MAX_POOL_SIZE: '2' }) },
  });
});

test('reserveConcurrency caps the API at 5 and the load function at 1', () => {
  const reserved = Template.fromStack(
    new SeqTransitStack(new App(), 'Reserved', { env, config: { ...config, reserveConcurrency: true } }),
  );
  reserved.hasResourceProperties('AWS::Lambda::Function', { Handler: 'SeqTransit.Api', ReservedConcurrentExecutions: 5 });
  reserved.hasResourceProperties('AWS::Lambda::Function', {
    Handler: 'SeqTransit.Ingestion::SeqTransit.Ingestion.LoadFunction::HandleAsync',
    ReservedConcurrentExecutions: 1,
  });
});

test('alarms and a monthly budget email the alert address', () => {
  template.resourceCountIs('AWS::CloudWatch::Alarm', 6);
  template.hasResourceProperties('AWS::SNS::Subscription', { Protocol: 'email', Endpoint: 'alerts@example.com' });
  template.hasResourceProperties('AWS::Budgets::Budget', {
    Budget: Match.objectLike({ BudgetLimit: { Amount: 10, Unit: 'USD' } }),
  });
});

test('API alarm detects HTTP 5xx responses even when the Lambda invocation succeeds', () => {
  template.hasResourceProperties('AWS::CloudWatch::Alarm', {
    Namespace: 'AWS/ApiGateway', MetricName: '5xx', Statistic: 'Sum', Period: 300, Threshold: 5,
  });
});

test('GitHub role trusts only the main branch of the configured repository', () => {
  const github = Template.fromStack(new GitHubDeployStack(new App(), 'GitHub', { env, config }));
  github.hasResourceProperties('AWS::IAM::Role', {
    AssumeRolePolicyDocument: Match.objectLike({
      Statement: Match.arrayWith([
        Match.objectLike({
          Condition: {
            StringEquals: { 'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com' },
            StringLike: {
              'token.actions.githubusercontent.com:sub': [
                'repo:someone/seq-transit-reliability:ref:refs/heads/main',
                'repo:someone@*/seq-transit-reliability@*:ref:refs/heads/main',
              ],
            },
          },
        }),
      ]),
    }),
  });
});
