@echo off
if [%~1]==[] goto :err

set NAMESPACE=%~1

echo Creating configurations from YAML files in [%~dp0/configs]
for %%f in (%~dp0/configs/*.yaml) do (
    echo "Deploying %%~nxf"
    kubectl apply -f "%~dp0/configs/%%~nxf" -n "%NAMESPACE%"
)

echo Creating environment-specific services from YAML files in [%~dp0]
for %%f in (%~dp0/*.yaml) do (
    echo "Deploying %%~nxf"
    kubectl apply -f "%~dp0/%%~nxf" -n "%NAMESPACE%"
)
goto :done

:err
echo Usage: deploy.cmd [NAMESPACE]

:done