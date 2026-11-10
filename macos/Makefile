APP := dist/AI Usage Bar.app

.PHONY: test build install run dump icon clean

test:
	swift test

# dist/AI Usage Bar.app + dist/AIUsageBar-<version>.dmg
build:
	./scripts/build.sh

# Copies the app to /Applications and opens it.
install: build
	-pkill -x AIUsageBar
	-pkill -x ClaudeUsageBar
	rm -rf "/Applications/AI Usage Bar.app"
	cp -R "$(APP)" /Applications/
	open "/Applications/AI Usage Bar.app"

run:
	swift run AIUsageBar

dump:
	swift run AIUsageBar --dump

icon:
	./scripts/make-icon.sh

clean:
	rm -rf .build dist
