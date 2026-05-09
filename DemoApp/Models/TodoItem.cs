using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DemoApp.Models
{
    public class TodoItem
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string Title { get; set; } = null!;

        public bool IsCompleted { get; set; }
    }
}
