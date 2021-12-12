#!/bin/bash
. /app/.env; /app/AndroidClientVersionWatcher >> /var/log/cron.log 2>&1
