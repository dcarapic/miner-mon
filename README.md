# Miner Mon
Miner mon(itor) is a small console application which monitors a (Monero) miner which is using a miner pool.  
If the miner stops working or freezes then the miner will be automatically restarted and an email will be sent.  
The application closes as soon as any key is pressed.

The detection if the miner works is done in the following way:
- If the miner executable is not working (but was working before) then the miner is considered not to be working
- If the miner did not contact the miner pool in the last 3 minutes (configurable) then the miner is considered not to be working

If any of these cases happen the miner will be stopped (if running) and then started again.

## Configuration
The following parameters can be configured:
- PoolStatsAddressUrl - The Url to be queried for the miner pool status. The response must a standard pool JSON which contains 'lastShare' information.
- SMTPServer - Address of the SMTP server for sending emails
- SMTPPort - SMTP port, by default this should be 25
- EmailRecipients - Semicolon separated list of email recipients
- StopMinerCommand - Command to stop the miner. 
- StartMinerCommand - Command to start the miner. 
- NotifyOnStartup - If 'true' then an email will be sent on startup
- MonitorName - Name of the monitor. This will be included in the email subject so that you may have multiple monitors and that you know which one sent the email.
- PoolMaximumLastUpdateTimeout - Maximum timespan to allow that the miner pool is updated before the miner would be restarted. 
- EmailSender - Email sender for notification emails.
- MinerExecutable - Full name of the miner executable (including the .exe). This is used to determine if the miner is working or not.




