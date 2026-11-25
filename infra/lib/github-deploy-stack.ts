import { CfnOutput, Duration, Stack, type StackProps } from 'aws-cdk-lib';
import * as iam from 'aws-cdk-lib/aws-iam';
import type { Construct } from 'constructs';
import type { AppConfig } from './config';
import { databaseUserArn } from './database-arn';

export interface GitHubDeployStackProps extends StackProps {
  readonly config: AppConfig;
  /** Set if the account already has a GitHub OIDC provider (only one is allowed per account). */
  readonly existingOidcProviderArn?: string;
}

/**
 * Deploy once, by hand. Lets GitHub Actions on the main branch of one repository deploy
 * without any long-lived AWS access keys: the workflow exchanges its OIDC token for this role.
 */
export class GitHubDeployStack extends Stack {
  constructor(scope: Construct, id: string, props: GitHubDeployStackProps) {
    super(scope, id, props);
    const repository = props.config.githubRepository;
    if (!repository) {
      throw new Error('Set githubRepository (owner/repo) in infra/cdk.context.json to deploy the GitHub stack.');
    }

    const provider = props.existingOidcProviderArn
      ? iam.OidcProviderNative.fromOidcProviderArn(this, 'GitHubOidc', props.existingOidcProviderArn)
      : new iam.OidcProviderNative(this, 'GitHubOidc', {
          url: 'https://token.actions.githubusercontent.com',
          clientIds: ['sts.amazonaws.com'],
        });

    const [owner, repo] = repository.split('/');
    // GitHub OIDC sub claims may include immutable numeric IDs (repo:owner@<id>/repo@<id>:ref:refs/heads/main).
    // A job that names a GitHub environment gets `:environment:<name>` instead of the ref. Deploys run
    // in `production` (required reviewer) and the weekly GTFS sync in `gtfs-sync`; both environments
    // allow only the main branch (README, "Deploy from GitHub Actions"). The ref patterns stay so a deploy keeps working while
    // the workflows switch over; remove them once every deploy runs in the environment.
    const subjects = (suffix: string) =>
      owner && repo ? [`repo:${repository}:${suffix}`, `repo:${owner}@*/${repo}@*:${suffix}`] : [`repo:${repository}:${suffix}`];
    const subPatterns = [
      ...subjects('ref:refs/heads/main'),
      ...subjects('environment:production'),
      ...subjects('environment:gtfs-sync'),
    ];

    const role = new iam.Role(this, 'DeployRole', {
      description: `GitHub Actions deploys for ${repository}`,
      maxSessionDuration: Duration.hours(1),
      assumedBy: new iam.WebIdentityPrincipal(provider.oidcProviderArn, {
        StringEquals: { 'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com' },
        StringLike: { 'token.actions.githubusercontent.com:sub': subPatterns },
      }),
    });

    // `cdk deploy` works by assuming the roles created by `cdk bootstrap`; nothing more is needed.
    role.addToPolicy(
      new iam.PolicyStatement({
        actions: ['sts:AssumeRole'],
        resources: [`arn:${this.partition}:iam::${this.account}:role/cdk-hnb659fds-*`],
      }),
    );

    // Migrations run as the cluster's admin user; the weekly GTFS sync only needs the ingest role,
    // which may replace the gtfs_* tables but cannot change the schema.
    role.addToPolicy(
      new iam.PolicyStatement({
        actions: ['rds-db:connect'],
        resources: [
          databaseUserArn(this, props.config.dbClusterResourceId, 'postgres'),
          databaseUserArn(this, props.config.dbClusterResourceId, 'seq_ingest'),
        ],
      }),
    );

    new CfnOutput(this, 'DeployRoleArn', { value: role.roleArn });
  }
}
