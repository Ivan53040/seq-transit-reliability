import { ArnFormat, Stack } from 'aws-cdk-lib';
import type { Construct } from 'constructs';

/** ARN that grants `rds-db:connect` for one database user on one Aurora cluster. */
export function databaseUserArn(scope: Construct, clusterResourceId: string, dbUser: string): string {
  return Stack.of(scope).formatArn({
    service: 'rds-db',
    resource: 'dbuser',
    resourceName: `${clusterResourceId}/${dbUser}`,
    arnFormat: ArnFormat.COLON_RESOURCE_NAME,
  });
}
