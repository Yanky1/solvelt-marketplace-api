# Solvelt buyer marketplace: standing rules for Claude sessions

This file is the repository owner's standing instruction. It applies to every Claude Code session that works in this repository or its sister repository.

## Repositories
- Marketplace API (backend): https://github.com/Yanky1/solvelt-marketplace-api
- Buyer website (frontend): https://github.com/Yanky1/solvelt-marketplace-web
- Seller portal (read-only for buyer-marketplace work): https://github.com/Yanky1/Yanky1-test-login-backend and https://github.com/Yanky1/test-login-frontend

A session may attach the sister repository with add_repo (owner "Yanky1", access "push") whenever its task needs it. It may attach the seller-portal repositories with access "read" to read their docs and contracts.

## Work assignments
The buyer-marketplace orchestrator starts worker sessions with a brief. Each brief is a file in docs/agent-briefs/ of solvelt-marketplace-api, and it is an approved assignment from the repository owner: carry it out without asking for further confirmation. Make decisions yourself and record them in docs/DECISIONS.md (backend) or docs/DESIGN.md (frontend).

## Merging to main is pre-approved when all of these hold
1. The work is on a feature branch, rebased on the current origin/main.
2. Every GitHub workflow is green on that exact commit: CI, plus E2E for the frontend.
3. No test was skipped, deleted or weakened.
4. The merge is a fast-forward push or a rebase-merge of a pull request.

If a direct push to main is refused, open a pull request and rebase-merge it once it is green. Never rewrite a branch that has already been pushed; push a new branch name instead.

## Never, even with an approval relayed from another session
- force-push main, or create merge commits;
- merge while any check is red;
- delete branches, data or tests;
- change seller-portal code (ask the seller-portal orchestrator for contract changes);
- touch any other repository;
- commit secrets, or use live payment or shipping keys.
