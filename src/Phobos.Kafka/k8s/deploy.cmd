@echo off
REM deploys all Kubernetes services to their staging environment

set NAMESPACE=phobos-kafka
set LOCAL=%~dp0
set base=%~dp0..\..\k8s

echo Creating Namespace [%NAMESPACE%]...
kubectl apply -f "%~dp0/namespace.yaml"

echo Using namespace [%NAMESPACE%] going forward...

call %base%\common\deploy.cmd %NAMESPACE%
call %base%\kafka\deploy.cmd %NAMESPACE%

echo Creating all services...
for %%f in (%LOCAL%/services/*.yaml) do (
    echo "Deploying %%~nxf"
    kubectl apply -f "%LOCAL%/services/%%~nxf" -n "%NAMESPACE%"
)

echo All services started. Printing K8s output..
kubectl get all -n "%NAMESPACE%"