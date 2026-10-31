namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The four kinds of kit test, as NUnit categories. Filter on one in the Test Runner window,
    /// with Unity's -testCategory argument or with Tools~/run_tests.py --category to run that
    /// kind alone.
    /// </summary>
    public static class TestCategories
    {
        /// <summary>Pure code: no GameInstance, database, scene or real asset.</summary>
        public const string Logic = "Logic";

        /// <summary>Needs a <see cref="TestGameInstance"/>, a TempSqliteDatabase or the network layer.</summary>
        public const string Harnessed = "Harnessed";

        /// <summary>Loads the real demo assets and checks rules that must always hold.</summary>
        public const string Content = "Content";

        /// <summary>Enters play mode. Slow; run before a release.</summary>
        public const string Play = "Play";
    }
}
