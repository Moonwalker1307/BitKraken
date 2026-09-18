-- BitKraken's notification helper.
--
-- macOS puts the icon of whichever bundle posted a notification on it and gives the poster no say,
-- which is why a notification from `osascript` arrives wearing Script Editor's icon. This applet is
-- a bundle that owns BitKraken's icon and nothing else: BitKraken runs it, the notification is
-- posted from inside this process, and the right face comes with it.
--
-- It posts the notification itself rather than being asked to. Telling this bundle to post one from
-- outside looks equivalent and is not: the Apple event needs LaunchServices to resolve the bundle
-- id, needs an applet to answer an event it has no handler for, and needs the user to grant BitKraken
-- automation access first. Running the applet needs none of the three.
--
-- The title and body arrive in the environment, never in this source. A torrent's name is arbitrary
-- text, and pasting arbitrary text into a script is how a name full of quotes stops being a name.

on run
	set theTitle to my environmentValue("BITKRAKEN_NOTIFY_TITLE")
	set theBody to my environmentValue("BITKRAKEN_NOTIFY_BODY")
	set outcome to "ok"

	try
		if theTitle is "" then
			set outcome to "no title"
		else
			display notification theBody with title theTitle
		end if
	on error whatWentWrong number howItWentWrong
		-- An unhandled error in an applet opens a dialog and waits for somebody to click it, which is
		-- a great deal worse than a missed notification - and BitKraken shows its own toast anyway.
		set outcome to "error " & howItWentWrong & ": " & whatWentWrong
	end try

	my trace(outcome)
end run

-- One environment variable, or "" when it is not set.
on environmentValue(theName)
	try
		return system attribute theName
	on error
		return ""
	end try
end environmentValue

-- Records where this ran from and how it went, for scripts/verify-macos-notifier.sh. The variable is
-- unset in every real run, so this does nothing but return.
on trace(outcome)
	try
		set destination to my environmentValue("BITKRAKEN_NOTIFY_TRACE")
		if destination is "" then return

		set report to (POSIX path of (path to me)) & linefeed & outcome & linefeed
		do shell script "printf %s " & quoted form of report & " > " & quoted form of destination
	end try
end trace
