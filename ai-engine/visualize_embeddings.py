import chromadb
from chromadb.utils import embedding_functions
import matplotlib.pyplot as plt
import matplotlib.cm as cm
from sklearn.decomposition import PCA
import numpy as np

client = chromadb.PersistentClient(path="./lpl_vectors")

embedding_fn = embedding_functions.OllamaEmbeddingFunction(
    url="http://localhost:11434/api/embeddings",
    model_name="nomic-embed-text"
)

collection = client.get_collection(
    name="lpl_documents",
    embedding_function=embedding_fn
)

results = collection.get(include=["embeddings", "documents", "metadatas"])
embeddings = np.array(results["embeddings"])
labels = [m["source"] for m in results["metadatas"]]

print(f"\nTotal chunks stored: {len(embeddings)}")

# Count chunks per source
from collections import Counter
counts = Counter(labels)
print("\nChunks per source:")
for source, count in counts.items():
    print(f"  {source}: {count} chunks")

# Add query vector
query = "What happens if an advisor misses compliance training?"
query_embedding = embedding_fn([query])
query_vec = np.array(query_embedding)

all_vectors = np.vstack([embeddings, query_vec])
all_labels = labels + ["★ YOUR QUERY"]

# Reduce to 2D
pca = PCA(n_components=2)
reduced = pca.fit_transform(all_vectors)

# Assign one color per unique source
unique_sources = list(set(labels))
colors = cm.Set1(np.linspace(0, 0.9, len(unique_sources)))
color_map = {source: colors[i] for i, source in enumerate(unique_sources)}

plt.figure(figsize=(12, 8))

# Plot each chunk individually
plotted_sources = set()
for i, (point, label) in enumerate(zip(reduced, all_labels)):
    if label == "★ YOUR QUERY":
        plt.scatter(point[0], point[1], c="red", s=250, marker="*", zorder=6)
        plt.annotate("  ★ YOUR QUERY", (point[0], point[1]),
                    fontsize=10, color="red", fontweight="bold")
    else:
        color = color_map[label]
        # Only add label to legend once per source
        legend_label = f"{label} ({counts[label]} chunks)" if label not in plotted_sources else ""
        plt.scatter(point[0], point[1], c=[color], s=80,
                   alpha=0.7, zorder=4, label=legend_label)
        # Label only first chunk of each source to avoid clutter
        if label not in plotted_sources:
            plt.annotate(f"  {label}", (point[0], point[1]),
                        fontsize=8, alpha=0.8)
        plotted_sources.add(label)

plt.title("ChromaDB Embedding Space — every chunk as its own dot", fontsize=12)
plt.xlabel("PCA dimension 1")
plt.ylabel("PCA dimension 2")
plt.legend(loc="lower right", fontsize=8)
plt.grid(True, alpha=0.3)
plt.tight_layout()
plt.savefig("embedding_space.png", dpi=150)
print("\nPlot saved — opening window...")
plt.show(block=True)