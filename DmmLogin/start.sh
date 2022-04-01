#!/bin/bash
. /app/.env; /app/DmmLogin 2>&1 | tee -a /var/log/cron.log
