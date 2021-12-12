#!/bin/bash
. /app/.env; /app/MainJsWatcher >> /var/log/cron.log 2>&1
