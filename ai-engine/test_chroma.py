import chromadb

# Creates a folder called 'lpl_vectors' on your machine
client = chromadb.PersistentClient(path="./lpl_vectors")

# Create a collection (like a database table)
collection = client.get_or_create_collection(
    name="lpl_documents",
    metadata={"description": "LPL financial documents"}
)

# Add a test document manually
collection.add(
    documents=["Advisors must report all transactions over $10,000 to compliance within 24 hours."],
    metadatas=[{"source": "compliance-manual", "department": "Compliance"}],
    ids=["doc_001"]
)

# Search for it
results = collection.query(
    query_texts=["what are the reporting requirements?"],
    n_results=1
)

print("Found:", results["documents"][0][0])
print("Source:", results["metadatas"][0][0]["source"])