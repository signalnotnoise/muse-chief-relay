# Legacy Python prototype

Optional reference implementation. The primary stack is:

- Chief: `src/Chief.Bridge` (.NET 8)
- Muse: `web/muse` (static browser client)

```bash
python3 -m pip install -r requirements.txt
cp ../../config.example.json config.json
# edit channel + nick
./bin/hc join
./bin/hc say "hello from chief"
./bin/hc status
```
