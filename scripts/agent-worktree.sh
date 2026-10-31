#!/usr/bin/env bash
# One isolated checkout per parallel agent. See AGENTS.md "Parallel agents".
#
#   scripts/agent-worktree.sh create <name>   # branch agent/<name> from HEAD
#   scripts/agent-worktree.sh remove <name>   # after its branch is merged
#
# Worktrees live outside OneDrive, in ~/zing-worktrees/<name>. node_modules is
# a junction to the main checkout's, so nothing is reinstalled.
set -euo pipefail

command=${1:?usage: agent-worktree.sh create|remove <name>}
name=${2:?usage: agent-worktree.sh create|remove <name>}
[[ $name =~ ^[a-z0-9-]+$ ]] || { echo "name must be lower-case letters, digits and dashes" >&2; exit 1; }

main=$(git worktree list --porcelain | sed -n '1s/^worktree //p')
root="$HOME/zing-worktrees"
tree="$root/$name"

case $command in
	create)
		[[ -e $tree ]] && { echo "$tree already exists" >&2; exit 1; }
		mkdir -p "$root"
		git -C "$main" worktree add -b "agent/$name" "$tree" HEAD
		cmd //c mklink //J "$(cygpath -w "$tree/node_modules")" "$(cygpath -w "$main/node_modules")" >/dev/null
		echo "$(cygpath -w "$tree")"
		;;
	remove)
		[[ -d $tree ]] || { echo "no worktree at $tree" >&2; exit 1; }
		# Unlink node_modules first. `git worktree remove --force` follows the
		# junction and deletes the shared packages (this happened once).
		if [[ -e $tree/node_modules ]]; then
			cmd //c rmdir "$(cygpath -w "$tree/node_modules")"
		fi
		[[ -e $tree/node_modules ]] && { echo "node_modules is still there; not removing" >&2; exit 1; }
		git -C "$main" worktree remove "$tree"
		;;
	*)
		echo "unknown command: $command" >&2
		exit 1
		;;
esac
