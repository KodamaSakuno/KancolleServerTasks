#!/bin/bash
/app/KcsConstWatcher 2>&1 | tee -a /var/log/cron.log
