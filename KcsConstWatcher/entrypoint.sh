#!/bin/bash
echo "export $(env | grep RedisHost | sed 's/=\(.*\)/="\1"/')" >> .env
echo "export $(env | grep DatabaseConn | sed 's/=\(.*\)/="\1"/')" >> .env
cron start
tail -f /var/log/cron.log
