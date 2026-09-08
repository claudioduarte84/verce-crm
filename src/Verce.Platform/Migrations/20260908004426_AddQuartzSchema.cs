using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <summary>
    /// Quartz.NET's official PostgreSQL ADO job store schema (ARCHITECTURE.md §6.1: "Persist the
    /// job store in PostgreSQL so schedules survive restarts"), adapted only to live in the
    /// `platform` schema per DATA-MODEL.md ("platform.qrtz_*... Category 4 technical tables").
    /// Verbatim from https://github.com/quartznet/quartznet database/tables/tables_postgres.sql
    /// except: schema-qualified as platform.*, and the destructive DROP-then-recreate preamble
    /// removed (this is a fresh migration, not a reset script). No column, type, key or index was
    /// altered — framework-owned technical schema is never converted to application conventions
    /// (ADR-0011 §1.3/§4: no IDomainEntity, no audit columns, no UUID v7 on these tables).
    /// </summary>
    public partial class AddQuartzSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE platform.qrtz_job_details
                  (
                    sched_name TEXT NOT NULL,
                    job_name TEXT NOT NULL,
                    job_group TEXT NOT NULL,
                    description TEXT NULL,
                    job_class_name TEXT NOT NULL,
                    is_durable BOOL NOT NULL,
                    is_nonconcurrent BOOL NOT NULL,
                    is_update_data BOOL NOT NULL,
                    requests_recovery BOOL NOT NULL,
                    job_data BYTEA NULL,
                    PRIMARY KEY (sched_name, job_name, job_group)
                );

                CREATE TABLE platform.qrtz_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    job_name TEXT NOT NULL,
                    job_group TEXT NOT NULL,
                    description TEXT NULL,
                    next_fire_time BIGINT NULL,
                    prev_fire_time BIGINT NULL,
                    priority INTEGER NULL,
                    trigger_state TEXT NOT NULL,
                    trigger_type TEXT NOT NULL,
                    start_time BIGINT NOT NULL,
                    end_time BIGINT NULL,
                    calendar_name TEXT NULL,
                    misfire_instr SMALLINT NULL,
                    misfire_orig_fire_time BIGINT NULL,
                    execution_group VARCHAR(200) NULL,
                    preferred_node VARCHAR(200) NULL,
                    preferred_node_auto BOOL NOT NULL DEFAULT FALSE,
                    retry_policy VARCHAR(250) NULL,
                    retry_attempt INTEGER NULL,
                    job_data BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, job_name, job_group)
                      REFERENCES platform.qrtz_job_details (sched_name, job_name, job_group)
                );

                CREATE TABLE platform.qrtz_simple_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    repeat_count BIGINT NOT NULL,
                    repeat_interval BIGINT NOT NULL,
                    times_triggered BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE platform.qrtz_simprop_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    str_prop_1 TEXT NULL,
                    str_prop_2 TEXT NULL,
                    str_prop_3 TEXT NULL,
                    int_prop_1 INTEGER NULL,
                    int_prop_2 INTEGER NULL,
                    long_prop_1 BIGINT NULL,
                    long_prop_2 BIGINT NULL,
                    dec_prop_1 NUMERIC NULL,
                    dec_prop_2 NUMERIC NULL,
                    bool_prop_1 BOOL NULL,
                    bool_prop_2 BOOL NULL,
                    time_zone_id TEXT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE platform.qrtz_cron_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    cron_expression TEXT NOT NULL,
                    time_zone_id TEXT,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE platform.qrtz_blob_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    blob_data BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE platform.qrtz_calendars
                  (
                    sched_name TEXT NOT NULL,
                    calendar_name TEXT NOT NULL,
                    calendar BYTEA NOT NULL,
                    PRIMARY KEY (sched_name, calendar_name)
                );

                CREATE TABLE platform.qrtz_paused_trigger_grps
                  (
                    sched_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_group)
                );

                CREATE TABLE platform.qrtz_paused_job_grps
                  (
                    sched_name TEXT NOT NULL,
                    job_group TEXT NOT NULL,
                    PRIMARY KEY (sched_name, job_group)
                );

                CREATE TABLE platform.qrtz_fired_triggers
                  (
                    sched_name TEXT NOT NULL,
                    entry_id TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    instance_name TEXT NOT NULL,
                    fired_time BIGINT NOT NULL,
                    sched_time BIGINT NOT NULL,
                    priority INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    job_name TEXT NULL,
                    job_group TEXT NULL,
                    is_nonconcurrent BOOL NOT NULL,
                    requests_recovery BOOL NULL,
                    execution_group VARCHAR(200) NULL,
                    PRIMARY KEY (sched_name, entry_id)
                );

                CREATE TABLE platform.qrtz_scheduler_state
                  (
                    sched_name TEXT NOT NULL,
                    instance_name TEXT NOT NULL,
                    last_checkin_time BIGINT NOT NULL,
                    checkin_interval BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, instance_name)
                );

                CREATE TABLE platform.qrtz_locks
                  (
                    sched_name TEXT NOT NULL,
                    lock_name TEXT NOT NULL,
                    PRIMARY KEY (sched_name, lock_name)
                );

                CREATE INDEX idx_qrtz_j_g_n ON platform.qrtz_job_details (sched_name, job_group, job_name);
                CREATE INDEX idx_qrtz_t_j ON platform.qrtz_triggers (sched_name, job_name, job_group);
                CREATE INDEX idx_qrtz_t_c ON platform.qrtz_triggers (sched_name, calendar_name);
                CREATE INDEX idx_qrtz_t_g_n ON platform.qrtz_triggers (sched_name, trigger_group, trigger_name);
                CREATE INDEX idx_qrtz_t_nft_st ON platform.qrtz_triggers (sched_name, trigger_state, next_fire_time asc, priority desc, misfire_instr);
                CREATE INDEX idx_qrtz_ft_inst_job_req_rcvry ON platform.qrtz_fired_triggers (sched_name, instance_name, requests_recovery);
                CREATE INDEX idx_qrtz_ft_j_g ON platform.qrtz_fired_triggers (sched_name, job_name, job_group);
                CREATE INDEX idx_qrtz_ft_t_g ON platform.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS platform.qrtz_fired_triggers;
                DROP TABLE IF EXISTS platform.qrtz_paused_trigger_grps;
                DROP TABLE IF EXISTS platform.qrtz_paused_job_grps;
                DROP TABLE IF EXISTS platform.qrtz_scheduler_state;
                DROP TABLE IF EXISTS platform.qrtz_locks;
                DROP TABLE IF EXISTS platform.qrtz_simprop_triggers;
                DROP TABLE IF EXISTS platform.qrtz_simple_triggers;
                DROP TABLE IF EXISTS platform.qrtz_cron_triggers;
                DROP TABLE IF EXISTS platform.qrtz_blob_triggers;
                DROP TABLE IF EXISTS platform.qrtz_triggers;
                DROP TABLE IF EXISTS platform.qrtz_job_details;
                DROP TABLE IF EXISTS platform.qrtz_calendars;
                """);
        }
    }
}
