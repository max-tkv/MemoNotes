using System;

namespace MemoNotes.Models
{
    public class ImageData
    {
        public string FilePath { get; set; }
        public string OriginalName { get; set; }
        public DateTime AddedDate { get; set; }
        
        public ImageData(string filePath, string originalName)
        {
            FilePath = filePath;
            OriginalName = originalName;
            AddedDate = DateTime.Now;
        }
    }
}