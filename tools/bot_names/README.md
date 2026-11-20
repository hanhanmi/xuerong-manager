# Bot profile names

These are the user-supplied name list, original pool, VPK rewrite reference, and method notes used for the v1.4.9 body package.

From this folder, create a named copy of a new upstream body ZIP with:

```powershell
python .\03_改人机名字_参考脚本.py --apply --zip "C:\path\to\CS2BotImprover.zip"
```

The script writes `CS2BotImprover_中文名.zip` next to the input. Use that ZIP as `--base` for `tools/assemble_resource_bundle.py`; the resource assembler never falls back to an older VPK.
