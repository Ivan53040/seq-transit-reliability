-- 0007: weekly AI-written reliability summaries (Phase 8, Amazon Bedrock).
--
-- One row per week_start (Monday in Brisbane time). Only replies that pass the numeric
-- guardrail are stored; if the model invents a number, the previous summary stays in place.

CREATE TABLE weekly_summary (
    week_start    date        PRIMARY KEY,
    summary_text  text        NOT NULL,
    input_figures jsonb       NOT NULL,
    model_id      text        NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE ON weekly_summary TO seq_ingest;
GRANT SELECT ON weekly_summary TO seq_api;
