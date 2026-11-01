import sys, os, UnityPy
src, out = sys.argv[1], sys.argv[2]
env = UnityPy.load(src)
n = 0
for obj in env.objects:
    if obj.type.name == "TextAsset":
        d = obj.read()
        name = d.m_Name or str(obj.path_id)
        data = d.m_Script
        if isinstance(data, str): data = data.encode("utf-8", "surrogateescape")
        with open(os.path.join(out, f"{name}.txt"), "wb") as f: f.write(data)
        n += 1
print("text assets:", n)
