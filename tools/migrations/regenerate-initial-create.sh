#!/usr/bin/env bash
# Regenerates InitialCreate from the CURRENT model (the migration is generated once, last, from the final main: M1-26/M1-83) and re-adds the one hand-written call.
# Run from the repository root with dotnet-ef 10.0.0 on the PATH (dotnet tool install --global dotnet-ef --version 10.0.0).
set -euo pipefail
dir=src/Solvelt.Marketplace.Api/Data/Migrations
rm -f "$dir"/*_InitialCreate.cs "$dir"/*_InitialCreate.Designer.cs "$dir"/MarketplaceDbModelSnapshot.cs
dotnet ef migrations add InitialCreate --project src/Solvelt.Marketplace.Api --startup-project tools/MigrationsTool --output-dir Data/Migrations
f=$(ls "$dir"/*_InitialCreate.cs | grep -v SearchSchema | head -1)
python3 - "$f" <<'PY'
import sys
p=sys.argv[1]; s=open(p,encoding='utf-8-sig').read()
i=s.index('protected override void Down')
j=s.rfind('}',0,i)                 # the brace that closes Up
s=s[:j]+'\n            CreateSearchSchema(migrationBuilder);\n        '+s[j:]
k=s.rstrip().rfind('}')            # namespace
k=s.rfind('}',0,k)                 # class
k=s.rfind('}',0,k)                 # the brace that closes Down
s=s[:k]+'\n            DropSearchSchema(migrationBuilder);\n        '+s[k:]
open(p,'w',encoding='utf-8').write(s)
PY
