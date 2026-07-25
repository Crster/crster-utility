using App.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace App.Services
{
    internal sealed class TodoRepository
    {
        private LiteDatabaseService Database => App.Settings.Database;

        public TodoDocument Create(string value, string category, string createdBy)
        {
            value = Required(value, nameof(value));
            category = Required(category, nameof(category));
            createdBy = createdBy.Trim().ToLowerInvariant();
            if (createdBy is not ("user" or "secretary")) throw new ArgumentOutOfRangeException(nameof(createdBy), "CreatedBy must be user or secretary.");
            var todo = new TodoDocument { Value = value, Category = category, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow };
            Database.Database.BeginTrans();
            try
            {
                Database.Todos.Insert(todo);
                if (Database.TodoCategories.FindById(category) is null)
                    Database.TodoCategories.Insert(new TodoCategoryDocument { Id = category });
                Database.Database.Commit();
                return todo;
            }
            catch { Database.Database.Rollback(); throw; }
        }

        public IReadOnlyList<TodoDocument> List() => Database.Todos.FindAll().OrderBy(item => item.IsDone).ThenByDescending(item => item.CreatedAt).ToList();
        public IReadOnlyList<TodoDocument> ListByCategory(string category) => Database.Todos.Find(item => item.Category == category).OrderByDescending(item => item.CreatedAt).ToList();

        public bool Update(TodoDocument todo)
        {
            todo.Value = Required(todo.Value, nameof(todo.Value));
            todo.Category = Required(todo.Category, nameof(todo.Category));
            if (todo.CreatedBy is not ("user" or "secretary")) throw new ArgumentOutOfRangeException(nameof(todo.CreatedBy));
            return Database.Todos.Update(todo);
        }

        public bool SetDone(string key, bool isDone)
        {
            var todo = Database.Todos.FindById(key);
            if (todo is null) return false;
            todo.IsDone = isDone;
            todo.DoneAt = isDone ? DateTime.UtcNow : null;
            return Database.Todos.Update(todo);
        }

        public bool Delete(string key) => Database.Todos.Delete(key);
        public IReadOnlyList<TodoCategoryDocument> ListCategories() => Database.TodoCategories.FindAll().OrderBy(item => item.Id).ToList();

        public void SetCategoryDescription(string category, string description) =>
            Database.TodoCategories.Upsert(new TodoCategoryDocument { Id = Required(category, nameof(category)), Description = description.Trim() });

        private static string Required(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
    }
}
