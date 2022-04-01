#!/bin/bash
/app/AndroidClientVersionWatcher 2>&1 | tee -a /var/log/cron.log
