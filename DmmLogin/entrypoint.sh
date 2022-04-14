#!/bin/sh
echo "export $(env | grep LoginId | sed 's/=\(.*\)/="\1"/')" >> .env
echo "export $(env | grep LoginPassword | sed 's/=\(.*\)/="\1"/')" >> .env
crond
tail -f /var/log/cron.log
