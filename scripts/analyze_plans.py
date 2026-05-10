import json, re
from collections import Counter
d = json.load(open('scripts/_plans2.json', encoding='utf-8-sig'))
rows = d['tables'][0]['rows']
def get_domains(payload):
    m = re.search(r'requiredDomains["\s:]*\[([^\]]*)\]', payload)
    if not m: return (None, [])
    inner = m.group(1)
    domains = re.findall(r'"(\w+)"', inner)
    return (len(domains), domains)
counts = Counter()
for r in rows:
    n, ds = get_domains(r[2])
    counts[n] += 1
    print(r[0], r[1][:19], '->', n, ds)
print('---')
print('Distribution:', dict(counts))
