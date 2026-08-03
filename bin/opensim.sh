#!/bin/sh


# Sockets/Files: Allow up to 65,535 concurrent network connection handles
ulimit -n 1048576
# ulimit -n 65535

# Thread Memory: Limit stack memory to 2,048 KB (2MB) per thread 
# ulimit -s 1048576
ulimit -s 2048



dotnet OpenSim.dll
