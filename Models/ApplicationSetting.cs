namespace NexusApp.Models;

/// <summary>
/// Represents application settings persisted in the database
/// </summary>
public class ApplicationSetting
{
    public int Id { get; set; }
    
    public string Key { get; set; } = string.Empty;
    
    public string Value { get; set; } = string.Empty;
    
    public SettingType Type { get; set; }
    
    public string Description { get; set; } = string.Empty;
    
    public DateTime LastModified { get; set; }

    public ApplicationSetting() { }

    public ApplicationSetting(string key, string value, SettingType type, string description = "")
    {
        Key = key;
        Value = value;
        Type = type;
        Description = description;
        LastModified = DateTime.UtcNow;
    }
}

public enum SettingType
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    Json = 4
}
