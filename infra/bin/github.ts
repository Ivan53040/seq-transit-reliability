import { App } from 'aws-cdk-lib';
import { loadConfig } from '../lib/config';
import { GitHubDeployStack } from '../lib/github-deploy-stack';

const app = new App();
new GitHubDeployStack(app, 'SeqTransitGitHubStack', {
  env: { account: process.env.CDK_DEFAULT_ACCOUNT, region: process.env.CDK_DEFAULT_REGION ?? 'ap-southeast-2' },
  config: loadConfig(app.node),
  existingOidcProviderArn: app.node.tryGetContext('existingOidcProviderArn'),
});
app.synth();
