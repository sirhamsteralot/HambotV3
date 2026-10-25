# The backup sidecar (design §5.2): runs independently of the bot so the database is
# backed up on schedule even while the bot container is stopped, restarting, or failing
# (FR-84). postgres:17-alpine provides pg_dump/pg_restore/psql matching the database
# server; rclone pushes dumps to S3-compatible offsite storage (FR-82); jq writes the
# shared status file (FR-100/FR-101) the bot's !health and watchdog consume.
FROM postgres:17-alpine

RUN apk add --no-cache bash rclone jq

COPY run-backup.sh /usr/local/bin/run-backup.sh
RUN chmod +x /usr/local/bin/run-backup.sh

# All configuration comes from environment (compose/.env); see run-backup.sh for the keys.
ENTRYPOINT ["/usr/local/bin/run-backup.sh"]
