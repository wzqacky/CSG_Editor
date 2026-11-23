using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class CSGStorage
{
    [Serializable]
    public sealed class CustomShape
    {
        public string name;
        public string state;
    }

    [Serializable]
    public class Entity
    {
        public int id;
        [SerializeReference] public CSGNode node;
        public Vector2 graphPosition;
        public bool hasGraphPosition;

        public Entity(int id, CSGNode node)
        {
            this.id = id;
            this.node = node;
        }
    }

    // Design choice: make m_entites private, such that add/delete cannot be affected by outside
    // Expose the list to outside through another public attribute
    [SerializeField] private List<Entity> m_entities = new List<Entity>();
    public IReadOnlyList<Entity> Entities => m_entities;
    [SerializeField] private List<CustomShape> m_customShapes = new List<CustomShape>();
    public IReadOnlyList<CustomShape> CustomShapes => m_customShapes;
    [SerializeField] private int m_nextId = 1;
    public int NextId => m_nextId;

    public void SetNextId(int nextId)
    {
        if (nextId < m_nextId)
            throw new ArgumentException("Next node ID must be greater than every existing ID.", nameof(nextId));
        m_nextId = nextId;
    }

    public Entity Add(CSGNode node)
    {
        if (node == null)
            throw new ArgumentNullException(nameof(node));

        return AddWithId(m_nextId, node);
    }

    public void AddCustomShape(string name, string state)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(state))
            throw new ArgumentException("A custom shape needs a name and a graph.");
        name = name.Trim();
        if (m_customShapes.Exists(shape => string.Equals(shape.name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A custom shape with that name already exists.");
        m_customShapes.Add(new CustomShape { name = name, state = state });
    }

    public bool RemoveCustomShape(CustomShape shape) => m_customShapes.Remove(shape);

    public Entity AddWithId(int id, CSGNode node)
    {
        if (node == null)
            throw new ArgumentNullException(nameof(node));
        if (id <= 0 || id == int.MaxValue || m_entities.Exists(entity => entity.id == id))
            throw new ArgumentException("Node IDs must be unique positive integers below the maximum.", nameof(id));
        var entity = new Entity(id, node);
        m_entities.Add(entity);
        m_nextId = Math.Max(m_nextId, id + 1);
        return entity;
    }

    public bool Remove(CSGNode node)
    {
        if (node == null)
            throw new ArgumentNullException(nameof(node));
        
        // Foreach entity, compare their reference key
        int index = m_entities.FindIndex(entity => ReferenceEquals(entity.node, node));
        if (index < 0) // Required node does not exist, cannot remove
            return false;

        m_entities.RemoveAt(index);
        return true;
    }
}