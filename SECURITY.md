# Security Policy

## Supported versions

Security fixes go into the latest release. Please update before reporting.

## Reporting a vulnerability

Please **don't** open a public issue for a security problem.

Report it privately with GitHub's [private vulnerability reporting](https://github.com/guruswarupa/PearUI/security/advisories/new) (Security tab → *Report a vulnerability*). Include:

- what the problem is and what an attacker could do with it,
- steps to reproduce it, and your Windows and PearUI versions,
- any proof of concept or logs that help.

You'll get a reply within a few days. We'll work on a fix, credit you if you'd like, and publish an advisory once a fixed release is available.

## What PearUI touches

PearUI runs as a normal user without administrator rights. A few opt-in features ask for permission once and only change what they say they will:

- **Performance page:** stops optional services and applies privacy tweaks, only the ones you tick.
- **Installer options:** Terminal styling, Linux commands (Git for Windows tools on your PATH) and start-up at sign-in.

PearUI collects no data about you. It only talks to GitHub: to check for new releases and download an update (you can turn the automatic check off in Settings → About), and to load the contributors, funding links and app list shown in Settings → Support and About. Those requests contain nothing about you beyond what any web request reveals, such as your IP address. The search box's "search the web" result opens your browser.
