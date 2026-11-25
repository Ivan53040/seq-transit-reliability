import { App } from 'aws-cdk-lib';
import { assertArtifactsExist, loadConfig } from '../lib/config';
import { SeqTransitStack } from '../lib/seq-transit-stack';

const app = new App();
const config = loadConfig(app.node);
assertArtifactsExist(config);
new SeqTransitStack(app, 'SeqTransitStack', {
  env: { account: process.env.CDK_DEFAULT_ACCOUNT, region: process.env.CDK_DEFAULT_REGION ?? 'ap-southeast-2' },
  config,
});
app.synth();
