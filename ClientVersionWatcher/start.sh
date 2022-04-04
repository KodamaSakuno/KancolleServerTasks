#!/bin/sh
/app/ClientVersionWatcher 2>&1 | tee -a /var/log/cron.log
