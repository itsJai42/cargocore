fx_version 'cerulean'
game 'gta5'
author 'jai'
version '0.1.0'
description 'Physical vehicle hauling and validated delivery prototype; OneSync required'
dependency '/onesync'
files { 'trailer.cfg', 'route.cfg', 'Client/bin/Release/net452/publish/*.dll' }
client_script 'Client/bin/Release/net452/publish/jai-trucking.Client.net.dll'
server_script 'Server/bin/Release/netstandard2.0/publish/jai-trucking.Server.net.dll'
