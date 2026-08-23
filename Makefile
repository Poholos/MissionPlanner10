DOTNET ?= dotnet
CONFIGURATION ?= Release
RID ?= linux-x64
OUTPUT_DIR ?= $(CURDIR)/out/packages
PUBLISH_PARENT ?= $(CURDIR)/out

export DOTNET CONFIGURATION RID OUTPUT_DIR PUBLISH_PARENT

.PHONY: linux-packages linux-tar linux-deb windows-packages windows-zip windows-msi

linux-packages:
	./build/linux/package.sh all

linux-tar:
	./build/linux/package.sh tar

linux-deb:
	./build/linux/package.sh deb

windows-packages:
	./build/windows/package.sh all

windows-zip:
	./build/windows/package.sh zip

windows-msi:
	./build/windows/package.sh msi
