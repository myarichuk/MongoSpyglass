using DemoApp.Models;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace DemoApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TodoController : ControllerBase
    {
        private readonly IMongoCollection<TodoItem> _todoItems;

        public TodoController(IMongoClient mongoClient)
        {
            var mongoDatabase = mongoClient.GetDatabase("TodoApp");
            _todoItems = mongoDatabase.GetCollection<TodoItem>("Todos");
        }

        [HttpGet]
        public async Task<List<TodoItem>> Get() =>
            await _todoItems.Find(_ => true).ToListAsync();

        [HttpGet("{id}")]
        public async Task<ActionResult<TodoItem>> Get(string id)
        {
            var todoItem = await _todoItems.Find(x => x.Id == id).FirstOrDefaultAsync();

            if (todoItem is null)
            {
                return NotFound();
            }

            return todoItem;
        }

        [HttpPost]
        public async Task<IActionResult> Post(TodoItem newTodoItem)
        {
            await _todoItems.InsertOneAsync(newTodoItem);
            return CreatedAtAction(nameof(Get), new { id = newTodoItem.Id }, newTodoItem);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, TodoItem updatedTodoItem)
        {
            var todoItem = await _todoItems.Find(x => x.Id == id).FirstOrDefaultAsync();

            if (todoItem is null)
            {
                return NotFound();
            }

            updatedTodoItem.Id = todoItem.Id;

            await _todoItems.ReplaceOneAsync(x => x.Id == id, updatedTodoItem);

            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var todoItem = await _todoItems.Find(x => x.Id == id).FirstOrDefaultAsync();

            if (todoItem is null)
            {
                return NotFound();
            }

            await _todoItems.DeleteOneAsync(x => x.Id == id);

            return NoContent();
        }
    }
}
