namespace CinemaGuard.Core.Speech;

public enum SpeechFamily { Phonon, Parakeet, Whisper }

/// <summary>One file to fetch: where from, where it lands (relative to the model's folder), its size and hash.</summary>
public sealed record DownloadFile(string Url, string RelativePath, long Size, string? Sha256 = null);

/// <summary>
/// One downloadable build of a model (int8 / fp16, q5 / q8, English-only). <see cref="DiskSize"/> is what it
/// takes on disk once installed; for Phonon, Fermion fetches the files itself, so the list is empty.
/// </summary>
public sealed record ModelVariant(string Id, string Label, IReadOnlyList<DownloadFile> Files, bool EnglishOnly, long DiskSize)
{
    public static ModelVariant FromFiles(string id, string label, bool englishOnly, params DownloadFile[] files) =>
        new(id, label, files, englishOnly, files.Sum(f => f.Size));
}

/// <summary>Where Fermion keeps a Phonon model inside FERMION_CACHE_DIR, and the name it answers to.</summary>
public sealed record PhononSpec(string Alias, string CacheFolder, string ModelFolder);

public sealed record SpeechModelInfo(
    string Id,
    SpeechFamily Family,
    string Name,
    string Languages,
    bool Older,
    IReadOnlyList<ModelVariant> Variants,
    PhononSpec? Phonon = null)
{
    public ModelVariant DefaultVariant => Variants[0];

    public ModelVariant Variant(string? id) => Variants.FirstOrDefault(v => v.Id == id) ?? DefaultVariant;

    public bool HasEnglishOnly => Variants.Any(v => v.EnglishOnly);
    public bool HasMultilingual => Variants.Any(v => !v.EnglishOnly);
}

/// <summary>
/// Every speech model the Models page offers. Sizes and SHA-256 come from the publishers' file lists
/// (Hugging Face LFS ids, Fermion's own catalogue), so a download is checked before it is used.
/// </summary>
public static class SpeechCatalog
{
    /// <summary>The one combination: Phonon 2.5 English, then Phonon 2.5 multilingual.</summary>
    public const string PairId = "pair-phonon-2.5";

    /// <summary>A transcription server (fermion serve, or an OpenAI-compatible provider).</summary>
    public const string RemoteId = "remote";

    public const string PhononEnglishId = "phonon-2.5-en";
    public const string PhononMultilingualId = "phonon-2.5";

    private const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private const string HuggingFace = "https://huggingface.co/";

    public static IReadOnlyList<SpeechModelInfo> Models { get; } =
    [
        Phonon(PhononEnglishId, "Phonon 2.5 English", "English", false, 177_757_399,
            "FermionResearch__Phonon-2.5-English", "model_phonon25_en_pen2u1125_int6"),
        Phonon(PhononMultilingualId, "Phonon 2.5", "25 languages", false, 177_871_642,
            "FermionResearch__Phonon-2.5", "model_phonon25_f25x1_48k_int6"),
        Phonon("phonon-2", "Phonon 2", "English", false, 177_716_675,
            "FermionResearch__Phonon-2", "model_phonon2_c4c_int6"),
        Phonon("phonon-1", "Phonon 1", "English", true, 454_527_730,
            "FermionResearch__Phonon-1", "model_v18_mlx_head8audio6_quint5"),
        Phonon("phonon-1-big", "Phonon 1 Big", "English", true, 822_143_951,
            "FermionResearch__Phonon-1-Big", "model_v18_mlx_quint5"),
        Phonon("phonon-1-micro", "Phonon 1 Micro", "English", true, 331_234_013,
            "FermionResearch__Phonon-1-Micro", "model_v18_mlx_hybrid4_quint5"),

        new("parakeet-tdt-0.6b-v3", SpeechFamily.Parakeet, "Parakeet TDT 0.6B v3", "25 European languages", false,
        [
            ModelVariant.FromFiles("int8", "int8", false,
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "tokens.txt", 93_939)),
            ModelVariant.FromFiles("fp32", "Full precision", false,
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3", "encoder.onnx", 41_766_257, "3eed7ce424bf8339ad09233533c687e2dbd07e74ccf5027b5e7344019ea373b0"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3", "encoder.weights", 2_435_420_160, "3af3f51af5f2d01dbbf5af47d42c7962a2c205f11004254bb4f2b979862f39a8"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3", "decoder.onnx", 47_233_743, "d593cdb0e571f5a457ec2219af9968cbf6b0e8198e8f7839b40a8754593bf68c"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3", "joiner.onnx", 25_286_330, "b9b0bcf88ac571902e69a6536223ed2d94885e981b85045410f1403d53121a63"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3", "tokens.txt", 93_939)),
        ]),
        new("parakeet-tdt-0.6b-v2", SpeechFamily.Parakeet, "Parakeet TDT 0.6B v2", "English", false,
        [
            ModelVariant.FromFiles("int8", "int8", true,
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "encoder.int8.onnx", 652_184_296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "decoder.int8.onnx", 7_257_753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "joiner.int8.onnx", 1_739_080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "tokens.txt", 9_384)),
            ModelVariant.FromFiles("fp16", "fp16", true,
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16", "encoder.fp16.onnx", 1_239_245_548, "bb1cc16e8223472b36bd637e2f77725d62b9895eaf609fec6cc91183e2753e38"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16", "decoder.fp16.onnx", 14_446_596, "45b04b71eb29dddeeb560e13a515291615bc541f8f5e9e336004956c0ce1a538"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16", "joiner.fp16.onnx", 3_456_459, "5d9d840eff9a3f3724ebce6d84c52038ee4664a08955c5a69d277f2d9733f9b1"),
                Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16", "tokens.txt", 9_384)),
        ]),

        Whisper("whisper-large-v3-turbo", "Whisper large-v3 turbo", false,
        [
            W("full", "Multilingual", false, "ggml-large-v3-turbo.bin", 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
            W("q8_0", "Multilingual · q8_0", false, "ggml-large-v3-turbo-q8_0.bin", 874_188_075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1"),
            W("q5_0", "Multilingual · q5_0", false, "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        ]),
        Whisper("whisper-small", "Whisper small", false,
        [
            W("full", "Multilingual", false, "ggml-small.bin", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
            W("q8_0", "Multilingual · q8_0", false, "ggml-small-q8_0.bin", 264_464_607, "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f"),
            W("q5_1", "Multilingual · q5_1", false, "ggml-small-q5_1.bin", 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"),
            W("en", "English only", true, "ggml-small.en.bin", 487_614_201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"),
            W("en-q8_0", "English only · q8_0", true, "ggml-small.en-q8_0.bin", 264_477_561, "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067"),
            W("en-q5_1", "English only · q5_1", true, "ggml-small.en-q5_1.bin", 190_098_681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30"),
        ]),
        Whisper("whisper-medium", "Whisper medium", true,
        [
            W("full", "Multilingual", false, "ggml-medium.bin", 1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
            W("q8_0", "Multilingual · q8_0", false, "ggml-medium-q8_0.bin", 823_369_779, "42a1ffcbe4167d224232443396968db4d02d4e8e87e213d3ee2e03095dea6502"),
            W("q5_0", "Multilingual · q5_0", false, "ggml-medium-q5_0.bin", 539_212_467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f"),
            W("en", "English only", true, "ggml-medium.en.bin", 1_533_774_781, "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356"),
            W("en-q8_0", "English only · q8_0", true, "ggml-medium.en-q8_0.bin", 823_382_461, "43fa2cd084de5a04399a896a9a7a786064e221365c01700cea4666005218f11c"),
            W("en-q5_0", "English only · q5_0", true, "ggml-medium.en-q5_0.bin", 539_225_533, "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0"),
        ]),
        Whisper("whisper-large-v3", "Whisper large-v3", true,
        [
            W("full", "Multilingual", false, "ggml-large-v3.bin", 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
            W("q5_0", "Multilingual · q5_0", false, "ggml-large-v3-q5_0.bin", 1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
        ]),
        Whisper("whisper-large-v2", "Whisper large-v2", true,
        [
            W("full", "Multilingual", false, "ggml-large-v2.bin", 3_094_623_691, "9a423fe4d40c82774b6af34115b8b935f34152246eb19e80e376071d3f999487"),
            W("q8_0", "Multilingual · q8_0", false, "ggml-large-v2-q8_0.bin", 1_656_129_691, "fef54e6d898246a65c8285bfa83bd1807e27fadf54d5d4e81754c47634737e8c"),
            W("q5_0", "Multilingual · q5_0", false, "ggml-large-v2-q5_0.bin", 1_080_732_091, "3a214837221e4530dbc1fe8d734f302af393eb30bd0ed046042ebf4baf70f6f2"),
        ]),
        Whisper("whisper-large-v1", "Whisper large-v1", true,
        [
            W("full", "Multilingual", false, "ggml-large-v1.bin", 3_094_623_691, "7d99f41a10525d0206bddadd86760181fa920438b6b33237e3118ff6c83bb53d"),
        ]),
        Whisper("whisper-base", "Whisper base", true,
        [
            W("full", "Multilingual", false, "ggml-base.bin", 147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
            W("q8_0", "Multilingual · q8_0", false, "ggml-base-q8_0.bin", 81_768_585, "c577b9a86e7e048a0b7eada054f4dd79a56bbfa911fbdacf900ac5b567cbb7d9"),
            W("q5_1", "Multilingual · q5_1", false, "ggml-base-q5_1.bin", 59_707_625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898"),
            W("en", "English only", true, "ggml-base.en.bin", 147_964_211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
            W("en-q8_0", "English only · q8_0", true, "ggml-base.en-q8_0.bin", 81_781_811, "a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e"),
            W("en-q5_1", "English only · q5_1", true, "ggml-base.en-q5_1.bin", 59_721_011, "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f"),
        ]),
        Whisper("whisper-tiny", "Whisper tiny", true,
        [
            W("full", "Multilingual", false, "ggml-tiny.bin", 77_691_713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
            W("q8_0", "Multilingual · q8_0", false, "ggml-tiny-q8_0.bin", 43_537_433, "c2085835d3f50733e2ff6e4b41ae8a2b8d8110461e18821b09a15c40c42d1cca"),
            W("q5_1", "Multilingual · q5_1", false, "ggml-tiny-q5_1.bin", 32_152_673, "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7"),
            W("en", "English only", true, "ggml-tiny.en.bin", 77_704_715, "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f"),
            W("en-q8_0", "English only · q8_0", true, "ggml-tiny.en-q8_0.bin", 43_550_795, "5bc2b3860aa151a4c6e7bb095e1fcce7cf12c7b020ca08dcec0c6d018bb7dd94"),
            W("en-q5_1", "English only · q5_1", true, "ggml-tiny.en-q5_1.bin", 32_166_155, "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b"),
        ]),
    ];

    public static SpeechModelInfo? Find(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static SpeechModelInfo PhononEnglish => Find(PhononEnglishId)!;
    public static SpeechModelInfo PhononMultilingual => Find(PhononMultilingualId)!;

    /// <summary>The selection ids the settings accept: a model id, the pair, or the server.</summary>
    public static bool IsKnownSelection(string? id) =>
        id == PairId || id == RemoteId || Find(id) is not null;

    private static SpeechModelInfo Phonon(string alias, string name, string languages, bool older, long unpacked, string cacheFolder, string modelFolder) =>
        new(alias, SpeechFamily.Phonon, name, languages, older,
            [new ModelVariant("default", "Default", [], !languages.Contains("languages", StringComparison.Ordinal), unpacked)],
            new PhononSpec(alias, cacheFolder, modelFolder));

    private static SpeechModelInfo Whisper(string id, string name, bool older, ModelVariant[] variants) =>
        new(id, SpeechFamily.Whisper, name, variants.Any(v => v.EnglishOnly) ? "99 languages, or English only" : "99 languages", older, variants);

    private static ModelVariant W(string id, string label, bool englishOnly, string file, long size, string sha256) =>
        ModelVariant.FromFiles(id, label, englishOnly, new DownloadFile(WhisperRepo + file, file, size, sha256));

    private static DownloadFile Hf(string repo, string file, long size, string? sha256 = null) =>
        new($"{HuggingFace}{repo}/resolve/main/{file}", file, size, sha256);
}
