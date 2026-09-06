set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

default: build

gen:
    just sync
    just gen_system
    just gen_bridge
    just gen_config

sync:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/sync.ps1" -ProjectRoot .

gen_system:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/gen.ps1" system -ProjectRoot .

gen_bridge:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/gen.ps1" bridge -ProjectRoot .

gen_config:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/gen.ps1" config -ProjectRoot .

config_check:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/gen.ps1" config_check -ProjectRoot .

config_pack:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/gen.ps1" config_pack -ProjectRoot .

check:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/gen.ps1" check -ProjectRoot .

test:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/test.ps1"

build:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/build.ps1" -ProjectRoot .

build-release:
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "./__FW_PATH__/tools/build.ps1" -ProjectRoot . -Release
