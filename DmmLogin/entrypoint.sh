#!/bin/bash
env | grep DatabaseConn >> /etc/environment
cron start
tail -f /var/log/cron.log
