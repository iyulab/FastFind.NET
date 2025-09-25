using FastFind.Models;

namespace FastFind.Extensions;

/// <summary>
/// FastFileItem 확장 메서드
/// </summary>
public static class FastFileItemExtensions
{
    /// <summary>
    /// FastFileItem을 FileItem으로 변환
    /// </summary>
    public static FileItem ToFileItem(this FastFileItem fastItem)
    {
        return new FileItem
        {
            FullPath = fastItem.FullPath,
            Name = fastItem.Name,
            DirectoryPath = fastItem.DirectoryPath,
            Extension = fastItem.Extension,
            Size = fastItem.Size,
            CreatedTime = fastItem.CreatedTime,
            ModifiedTime = fastItem.ModifiedTime,
            AccessedTime = fastItem.AccessedTime,
            Attributes = fastItem.Attributes,
            DriveLetter = fastItem.DriveLetter
        };
    }

    /// <summary>
    /// FileItem을 FastFileItem으로 변환
    /// </summary>
    public static FastFileItem ToFastFileItem(this FileItem item)
    {
        return new FastFileItem(
            item.FullPath,
            item.Name,
            item.DirectoryPath,
            item.Extension,
            item.Size,
            item.CreatedTime,
            item.ModifiedTime,
            item.AccessedTime,
            item.Attributes,
            item.DriveLetter
        );
    }
}