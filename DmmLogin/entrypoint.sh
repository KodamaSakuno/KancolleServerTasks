#!/bin/bash
echo "export $(env | grep DatabaseConn)" >> .env
cron start
tail -f /var/log/cron.log
