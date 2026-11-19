#!/usr/bin/env python3
"""Paridade de contrato Nest x .NET, por módulo (guia do Strangler Fig, plano §16).

Uso: python3 tools/api_parity.py <openapi do Nest> [openapi do .NET] > docs/paridade-api.md
Compara MÉTODO + CAMINHO (parâmetros normalizados para {}). Não compara schemas: isso é papel dos testes
de contrato de cada rota migrada.
"""
import json
import re
import sys
from collections import defaultdict

METHODS = ("get", "post", "put", "patch", "delete")


def operations(path):
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    ops = set()
    for route, item in document["paths"].items():
        normalized = re.sub(r"\{[^}]+\}", "{}", route.rstrip("/"))
        for method in item:
            if method in METHODS:
                ops.add((method.upper(), normalized))
    return ops


def module(route):
    parts = route.split("/")
    return parts[3] if len(parts) > 3 and parts[1] == "api" else parts[1]


def main():
    nest = operations(sys.argv[1])
    dotnet = operations(sys.argv[2] if len(sys.argv) > 2 else "docs/openapi/v1.json")
    by_module = defaultdict(lambda: {"nest": set(), "dotnet": set()})
    for op in nest:
        by_module[module(op[1])]["nest"].add(op)
    for op in dotnet:
        by_module[module(op[1])]["dotnet"].add(op)

    covered = nest & dotnet
    print("# Paridade de contrato: Nest x .NET")
    print()
    print("Gerado por `tools/api_parity.py` a partir dos dois `openapi.json` (método + caminho; parâmetros normalizados).")
    print("Guia do Strangler Fig: um módulo com 100% pode ter o tráfego roteado para a API .NET no BFF.")
    print()
    print(f"**Cobertura geral: {len(covered)} de {len(nest)} operações do Nest ({100 * len(covered) // max(1, len(nest))}%).** "
          f"A API .NET tem {len(dotnet - nest)} operações que o Nest não tem (rotas novas ou desvios documentados nas ADRs).")
    print()
    print("| Módulo | Nest | Já no .NET | % | Situação |")
    print("|---|---:|---:|---:|---|")
    rows = []
    for name, ops in by_module.items():
        if not ops["nest"]:
            continue
        done = len(ops["nest"] & ops["dotnet"])
        pct = 100 * done // len(ops["nest"])
        status = "pronto para rotear" if pct == 100 else ("parcial" if done else "não portado")
        rows.append((-pct, name, len(ops["nest"]), done, pct, status))
    for _, name, total, done, pct, status in sorted(rows):
        print(f"| `{name}` | {total} | {done} | {pct}% | {status} |")

    print()
    print("## Operações do Nest ainda ausentes nos módulos parciais")
    print()
    for _, name, total, done, pct, status in sorted(rows):
        if status != "parcial":
            continue
        missing = sorted(by_module[name]["nest"] - by_module[name]["dotnet"], key=lambda op: (op[1], op[0]))
        print(f"<details><summary><code>{name}</code>: {len(missing)} faltando</summary>")
        print()
        for method, route in missing:
            print(f"- `{method} {route}`")
        print()
        print("</details>")
        print()

    print("## Só na API .NET")
    print()
    for method, route in sorted(dotnet - nest, key=lambda op: (op[1], op[0])):
        print(f"- `{method} {route}`")


if __name__ == "__main__":
    main()
