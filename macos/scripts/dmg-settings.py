# Layout of the .dmg window, read by dmgbuild (https://dmgbuild.readthedocs.io)
# from scripts/build.sh: the app on the left, Applications on the right, over
# Support/dmg-background.tiff (see scripts/make-dmg-background.swift).
#
# dmgbuild writes the window settings itself, without scripting Finder, so the
# same layout comes out on a CI machine with no screen.
import os.path

application = defines["app"]  # noqa: F821 (provided by dmgbuild -D)
app_name = os.path.basename(application)

format = "UDZO"
files = [application]
symlinks = {"Applications": "/Applications"}
icon = defines["volume_icon"]  # noqa: F821
background = defines["background"]  # noqa: F821

# Finder counts the title bar in this size: 428 leaves about 400 points for
# the background, which is drawn 430 tall so nothing shows below it.
window_rect = ((200, 140), (640, 428))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False

icon_size = 112
text_size = 13
icon_locations = {
    app_name: (170, 200),
    "Applications": (470, 200),
}
