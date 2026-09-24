namespace OsuCollectionManager.Osu;

public sealed record Collection(string Name, List<string> Hashes);

public sealed class CollectionDatabase
{
    public int Version { get; set; }
    public List<Collection> Collections { get; } = [];

    public static CollectionDatabase Read(string path)
    {
        var db = new CollectionDatabase();
        if (!File.Exists(path))
        {
            db.Version = 20250107;
            return db;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new OsuReader(fs);
        db.Version = r.ReadInt32();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadOsuString() ?? "";
            int n = r.ReadInt32();
            var hashes = new List<string>(n);
            for (int j = 0; j < n; j++)
                if (r.ReadOsuString() is { } h) hashes.Add(h);
            db.Collections.Add(new Collection(name, hashes));
        }
        return db;
    }

    /// <summary>Writes to a temp file first and keeps a timestamped backup of the previous file.</summary>
    public string? Write(string path)
    {
        string? backup = null;
        if (File.Exists(path))
        {
            backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
        }

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new OsuWriter(fs))
        {
            w.Write(Version);
            w.Write(Collections.Count);
            foreach (var c in Collections)
            {
                w.WriteOsuString(c.Name);
                w.Write(c.Hashes.Count);
                foreach (var h in c.Hashes) w.WriteOsuString(h);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return backup;
    }

    public Collection GetOrCreate(string name)
    {
        var c = Collections.FirstOrDefault(x => x.Name == name);
        if (c is null)
        {
            c = new Collection(name, []);
            Collections.Add(c);
        }
        return c;
    }
}
