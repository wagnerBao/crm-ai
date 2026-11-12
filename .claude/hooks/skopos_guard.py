#!/usr/bin/env python3
"""PreToolUse guard shared by Claude Code and Codex (stdlib only, Python 3.9+).

Claude Code identifies subagents, so risky calls ask in the main session and are denied
in specialists. Codex has no agent identity in PreToolUse and treats "ask" as a failed
hook that lets the call continue, so there the guard only denies. This is a guardrail,
not a security boundary: approvals and sandbox stay active in both runners.
"""

import argparse
import fnmatch
import json
from pathlib import Path
import re
import sys

COMMAND_START = r"(?:^|[;&|(\n\"'`]\s*)"
GIT = COMMAND_START + r"git(?:\s+-[cC]\s+\S+|\s+--?[\w-]+(?:=\S+)?)*\s+"
RISKY = (
    (GIT + r"push\b", "git push"),
    (GIT + r"reset\s+--hard\b", "git reset --hard"),
    (GIT + r"clean\s+-\w*f", "git clean -f"),
    (COMMAND_START + r"docker\s+(?:push|stack\s+deploy|service\s+update)\b", "publicação Docker"),
    (COMMAND_START + r"gh\s+(?:workflow\s+run|release\s+create|pr\s+merge)\b", "ação de publicação no GitHub"),
    (COMMAND_START + r"(?:kubectl|helm)\s", "alteração de cluster"),
)
REMOTE = COMMAND_START + r"(?:sshpass|ssh|scp|sftp|rsync)\s"
COMMIT = GIT + r"commit\b"
PATH_TOOLS = {"Read", "Edit", "Write", "MultiEdit", "NotebookEdit", "Grep", "Glob"}
WRITE_TOOLS = {"Edit", "Write", "MultiEdit", "NotebookEdit"}
COORDINATOR_FILES = {"delivery.json", "DELIVERY.md", "delivery.env"}


def bash_verdict(command, runner, subagent):
    for pattern, label in RISKY:
        if re.search(pattern, command):
            if runner == "codex":
                return "deny", f"{label} bloqueado: execute manualmente fora do agente, se foi pedido."
            if subagent:
                return "deny", f"{label} bloqueado para especialistas: devolva ao coordenador."
            return "ask", f"{label} exige confirmação explícita."
    # Codex cannot tell the coordinator from a specialist, and read-only production
    # diagnosis over SSH is a documented routine, so these stay with its approvals.
    if runner == "codex":
        return None
    if re.search(REMOTE, command):
        if subagent:
            return "deny", "Acesso remoto bloqueado para especialistas: peça evidências ao coordenador."
        return "ask", "Acesso remoto: confirme que é leitura de diagnóstico."
    if subagent and re.search(COMMIT, command):
        return "deny", "Especialistas não fazem commit: devolva o diff ao coordenador."
    if subagent and ".agent-local" in command:
        return "deny", "Credenciais locais não são acessíveis a especialistas."
    return None


def tool_path(tool_input):
    for key in ("file_path", "notebook_path", "path"):
        value = tool_input.get(key)
        if isinstance(value, str) and value:
            return value
    return None


def delivery_root(path):
    for parent in path.parents:
        if parent.parent.name == ".deliveries" and (parent / "delivery.json").is_file():
            return parent
    return None


def ownership_verdict(path, agent):
    root = delivery_root(path)
    if root is None:
        return None
    relative = path.relative_to(root).as_posix()
    if relative in COORDINATOR_FILES:
        return "deny", "Arquivos da entrega são escritos apenas pelo coordenador."
    owners = json.loads((root / "delivery.json").read_text(encoding="utf-8")).get("owners", {})
    matches = [name for name, patterns in owners.items() if any(fnmatch.fnmatchcase(relative, p) for p in patterns)]
    if agent not in matches:
        return "deny", f"{relative} não foi atribuído a {agent or 'este agente'} em delivery.json."
    if len(matches) > 1:
        return "deny", f"{relative} tem mais de um escritor: {', '.join(matches)}."
    return None


def path_verdict(payload, subagent):
    tool = payload.get("tool_name")
    if not subagent or tool not in PATH_TOOLS:
        return None
    raw = tool_path(payload.get("tool_input") or {})
    if raw is None:
        return None
    path = Path(payload.get("cwd") or ".").joinpath(raw).resolve()
    if ".agent-local" in path.parts:
        return "deny", "Credenciais locais não são acessíveis a especialistas."
    if tool in WRITE_TOOLS:
        return ownership_verdict(path, payload.get("agent_type"))
    return None


def verdict(payload, runner):
    subagent = bool(payload.get("agent_id"))
    if payload.get("tool_name") == "Bash":
        command = (payload.get("tool_input") or {}).get("command") or ""
        return bash_verdict(command, runner, subagent)
    if runner == "claude":
        return path_verdict(payload, subagent)
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner", choices=("claude", "codex"), required=True)
    runner = parser.parse_args().runner
    try:
        result = verdict(json.load(sys.stdin), runner)
    except Exception as exc:  # A broken guard must not silently allow a risky call.
        result = ("deny", f"skopos_guard falhou ({type(exc).__name__}); corrija o hook antes de continuar.")
    if result:
        decision, reason = result
        json.dump({"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
            "permissionDecisionReason": reason,
        }}, sys.stdout, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
