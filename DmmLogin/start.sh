#!/bin/bash
. /app/.env; /app/DmmLogin >> /var/log/cron.log 2>&1
