#!/bin/bash
. /app/.env; /app/KcsConstWatcher >> /var/log/cron.log 2>&1
