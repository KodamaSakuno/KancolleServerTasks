#!/bin/sh
/app/ApiStart2Watcher 2>&1 | tee -a /var/log/cron.log
