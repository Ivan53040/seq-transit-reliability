import * as fs from 'node:fs';
import * as path from 'node:path';
import type { Node } from 'constructs';

/**
 * Settings for the stacks. Each value comes from CDK context (infra/cdk.context.json, which is
 * git-ignored, or `-c key=value`) or, failing that, an environment variable (used in CI).
 */
export interface AppConfig {
  /** Writer endpoint of the Aurora express-configuration cluster. */
  readonly dbHost: string;
  /** DbClusterResourceId (cluster-XXXX), used to scope IAM database sign-in. */
  readonly dbClusterResourceId: string;
  /** Where alarm and budget emails go. Optional; without it no email alerts are created. */
  readonly alertEmail?: string;
  readonly monthlyBudgetUsd: number;
  /** How often snapshots are loaded into the database. Longer means Aurora sleeps more. */
  readonly loadIntervalMinutes: number;
  readonly rawSnapshotRetentionDays: number;
  /**
   * Reserve Lambda concurrency (API 5, load 1). Off by default: AWS keeps 10 executions unreserved,
   * so an account on the new-account quota of 10 cannot reserve any. Without it, the load function
   * takes a database advisory lock instead, and the API pool is capped at DB_MAX_POOL_SIZE.
   */
  readonly reserveConcurrency: boolean;
  /** owner/repo allowed to deploy through GitHub Actions. */
  readonly githubRepository?: string;
  /** Build outputs the stack packages. Produced by scripts/publish.(ps1|sh). */
  readonly artifacts: {
    readonly ingestion: string;
    readonly api: string;
    readonly web: string;
  };
}

const repoRoot = path.resolve(__dirname, '..', '..');

export function loadConfig(node: Node): AppConfig {
  const value = (key: string, envName: string): string | undefined => {
    const fromContext = node.tryGetContext(key);
    const raw = fromContext !== undefined && fromContext !== '' ? String(fromContext) : process.env[envName];
    return raw && raw.trim().length > 0 ? raw.trim() : undefined;
  };

  return {
    dbHost: value('dbHost', 'SEQ_DB_HOST') ?? missing('dbHost', 'SEQ_DB_HOST'),
    dbClusterResourceId:
      value('dbClusterResourceId', 'SEQ_DB_CLUSTER_RESOURCE_ID') ?? missing('dbClusterResourceId', 'SEQ_DB_CLUSTER_RESOURCE_ID'),
    alertEmail: value('alertEmail', 'SEQ_ALERT_EMAIL'),
    monthlyBudgetUsd: Number(value('monthlyBudgetUsd', 'SEQ_MONTHLY_BUDGET_USD') ?? 10),
    loadIntervalMinutes: Number(value('loadIntervalMinutes', 'SEQ_LOAD_INTERVAL_MINUTES') ?? 30),
    rawSnapshotRetentionDays: Number(value('rawSnapshotRetentionDays', 'SEQ_RAW_RETENTION_DAYS') ?? 14),
    reserveConcurrency: (value('reserveConcurrency', 'SEQ_RESERVE_CONCURRENCY') ?? 'false').toLowerCase() === 'true',
    githubRepository: value('githubRepository', 'SEQ_GITHUB_REPOSITORY'),
    artifacts: {
      ingestion: path.join(repoRoot, 'artifacts', 'ingestion'),
      api: path.join(repoRoot, 'artifacts', 'api'),
      web: path.join(repoRoot, 'web', 'dist'),
    },
  };
}

/** Fails early with a useful message instead of a confusing asset error deep in synth. */
export function assertArtifactsExist(config: AppConfig): void {
  const absent = Object.values(config.artifacts).filter((dir) => !fs.existsSync(dir));
  if (absent.length > 0) {
    throw new Error(
      `Build outputs not found:\n  ${absent.join('\n  ')}\nRun scripts/publish.ps1 (Windows) or scripts/publish.sh first.`,
    );
  }
}

function missing(key: string, envName: string): never {
  throw new Error(
    `Missing setting "${key}". Put it in infra/cdk.context.json, pass -c ${key}=..., or set ${envName}. ` +
      'scripts/create-database prints the values after creating the cluster.',
  );
}
