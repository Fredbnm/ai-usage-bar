APP := dist/Claude Usage.app

.PHONY: test build install run dump icon clean

test:
	swift test

# dist/Claude Usage.app + dist/ClaudeUsage-<versão>.dmg
build:
	./scripts/build.sh

# Copies the app to /Applications and opens it.
install: build
	-pkill -x ClaudeUsageBar
	rm -rf "/Applications/Claude Usage.app"
	cp -R "$(APP)" /Applications/
	open "/Applications/Claude Usage.app"

run:
	swift run ClaudeUsageBar

dump:
	swift run ClaudeUsageBar --dump

icon:
	./scripts/make-icon.sh

clean:
	rm -rf .build dist
