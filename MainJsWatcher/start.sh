#!/bin/sh
. /app/.env; /app/MainJsWatcher 2>&1 | tee -a /var/log/cron.log
