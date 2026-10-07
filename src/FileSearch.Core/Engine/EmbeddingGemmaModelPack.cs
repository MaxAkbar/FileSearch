namespace FileSearch.Core.Engine;

internal static class EmbeddingGemmaModelPack
{
    internal const string Id = "embeddinggemma-300m-q4-onnx";
    internal const string Revision = "5090578d9565bb06545b4552f76e6bc2c93e4a66";
    private const string Repository = "https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX";

    public static EmbeddingModelPackCatalogEntry Entry { get; } = new(
        new EmbeddingModelPackManifest
        {
            FormatVersion = 2,
            Id = Id,
            DisplayName = "Google EmbeddingGemma 300M (q4)",
            Version = Revision,
            Description = "Optional multilingual document and OCR text search. 768 dimensions, 2048 tokens. Rebuild Smart Search after switching models; relevance scores vary by model.",
            License = "Gemma Terms of Use",
            LicenseUrl = "https://ai.google.dev/gemma/terms",
            RequiresLicenseAcceptance = true,
            SourceUrl = $"{Repository}/tree/{Revision}",
            ModelFile = "onnx/model_q4.onnx",
            VocabularyFile = "tokenizer.json",
            TokenizerKind = "huggingface-gemma",
            DoLowerCase = false,
            Dimension = 768,
            MaxTokens = 2048,
            QueryPrefix = "task: search result | query: ",
            DocumentPrefix = "title: none | text: ",
            TokenTypeIdsName = string.Empty,
            OutputName = "sentence_embedding",
            Pooling = EmbeddingModelPooling.SentenceEmbedding,
            Normalize = true,
            QuantizationVersion = "q4-fp32-hf-tokenizers-0.22.2-v1",
            MaximumBatchSize = 4,
            MaximumPaddedTokensPerBatch = 2048,
            Files =
            [
                Artifact("onnx/model_q4.onnx", "ad1dfee81a70f7944b9b9d1cc6e48075b832881cf33fab2f2b248be78f3f0043", 519322),
                Artifact("onnx/model_q4.onnx_data", "599962c3143b040de2dd05e5975be3e9091dd067cacc6a8f7186e3203bab9e02", 196725760),
                Artifact("tokenizer.json", "4dda02faaf32bc91031dc8c88457ac272b00c1016cc679757d1c441b248b9c47", 20323312),
                Artifact("tokenizer.model", "1299c11d7cf632ef3b4e11937501358ada021bbdf7c47638d13c0ee982f2e79c", 4689074),
                Artifact("tokenizer_config.json", "3ca953eea6c3c9fcda9cf3df22949ff18b216f7c74bd6459230f3f1013953f3a", 1156830),
                Artifact("special_tokens_map.json", "2f7b0adf4fb469770bb1490e3e35df87b1dc578246c5e7e6fc76ecf33213a397", 662),
                Artifact("config.json", "6e1f06404b7163e0325ed2ea3e6781cde50f4a50b31780a95ad0d30e8404d77b", 1765),
                Notice("Gemma-Terms-of-Use.txt", "dc436852e9f48094be6c0fa13ce4edced3738bd381eabc5bc3e1047057749a62", 9085),
                Notice("Gemma-Prohibited-Use-Policy.txt", "a7ffa4c6bccc6055c9e1ab0afca0220ccd6bf9af4fcd73d7cd82bd1f1cefbff4", 3762),
                Notice("Gemma-NOTICE.txt", "1c654c7a1aa5e38a056ba834835600e53f84c4f6d3a8154a63bfbc9ac9735ac6", 388),
            ],
        },
        IsRecommended: false,
        InstallSizeLabel: "About 224 MB (214 MiB)");

    private static EmbeddingModelPackFile Artifact(string path, string sha256, long bytes) =>
        new(path, $"{Repository}/resolve/{Revision}/{path}", sha256, bytes);

    private static EmbeddingModelPackFile Notice(string name, string sha256, long bytes) =>
        new(name == "Gemma-NOTICE.txt" ? "NOTICE" : name, $"embedded:FileSearch.Core.Licenses.{name}", sha256, bytes);
}
