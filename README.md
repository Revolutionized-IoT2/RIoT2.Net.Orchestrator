# RIoT2.Net.Orchestrator

# Debugging / running locally
Update Environment parameters in Properties/launchSettings for RIoT2.Net.Orchestrator -profile 
Start RIoT2.Net.Orchestrator -profile in debugging mode

# Docker commands
docker build -t riot2-orchestrator . --build-arg NUGET_AUTH_TOKEN={token}
docker save riot2-orchestrator > riot2-orchestrator.tar

# Container folders
/app/StoredObjects