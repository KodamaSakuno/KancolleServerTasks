#!/bin/bash
DatabaseConn=TEST echo "export $(env | grep DatabaseConn)"
cron start
tail -f /var/log/cron.log
