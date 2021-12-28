#!/bin/bash
echo "export $(env | grep LoginId | sed 's/=\(.*\)/="\1"/')" >> .env
echo "export $(env | grep LoginPassword | sed 's/=\(.*\)/="\1"/')" >> .env
cron start
tail -f /var/log/cron.log
