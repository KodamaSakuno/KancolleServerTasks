#!/bin/bash
. /app/.env; /app/ClientVersionWatcher >> /var/log/cron.log 2>&1
