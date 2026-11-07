"""Makes reference.json: the token ids and the vectors that the tools of the model's authors give (tokenizers and ONNX Runtime), which ModelReferenceFixture compares the .NET tokenizer and
provider with. Run in the folder of the model: pip install tokenizers onnxruntime numpy; python reference.py"""
import json, numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
tok = Tokenizer.from_file('tokenizer.json')
texts = [
 "Hello world",
 "Comment sauvegarder la base de données chaque nuit ?",
 "Les sauvegardes sont écrites dans le dossier backup et gardées sept jours.",
 "def main():\n    print('héllo')  # commentaire\n",
 "Émile Zola — « J'accuse… » 1898, naïve café; 日本語のテキスト 🙂",
 "   leading and trailing   spaces\t\ttabs\n\nnewlines  ",
 "It's we're they'll I'd she'd can't 12345 6789012 3.14159",
 "# Title\n\n- item one\n- item two\n\n```rust\nfn main() {}\n```\n",
 "a",
 "",
]
sess = ort.InferenceSession('model.onnx', providers=['CPUExecutionProvider'])
print([i.name for i in sess.get_inputs()], [o.name for o in sess.get_outputs()])
out = []
for t in texts:
    enc = tok.encode(t)
    ids = enc.ids
    r = sess.run(None, {'input_ids': np.array([ids], dtype=np.int64), 'attention_mask': np.ones((1, len(ids)), dtype=np.int64)})
    shapes=[x.shape for x in r]
    h = r[0][0]            # [seq, 384]
    v = h[0]; v = v / np.linalg.norm(v)
    out.append({'text': t, 'ids': ids, 'embedding': [float(x) for x in v]})
    print(len(ids), shapes)
json.dump(out, open('ref.json', 'w'))
